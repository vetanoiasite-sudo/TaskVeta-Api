using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Organization;

public record UserDto(int Id, string FullName, string Email, bool IsActive, string[] Roles, int? EmployeeId,
    string? EmployeeCode, DateTime? LastLoginAt, bool IsLockedOut, DateTime CreatedAt);

public record RoleDto(int Id, string Name, string Description, string[] Permissions, int UserCount);

public class UserQuery : PagedRequest
{
    public string? Role { get; set; }
    public bool? IsActive { get; set; }
}

public record UpdateUserRequest(string FullName, string Email, string[] Roles, bool IsActive, int? EmployeeId);
public record ResetPasswordRequest(string NewPassword);

public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Roles).NotEmpty().WithMessage("At least one role is required.");
        RuleForEach(x => x.Roles).Must(r => Domain.Security.Roles.All.Contains(r)).WithMessage("Unknown role '{PropertyValue}'.");
    }
}

public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator() => RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128);
}

public interface IUserService
{
    Task<PagedResult<UserDto>> GetPagedAsync(UserQuery query, CancellationToken ct = default);
    Task<UserDto> GetAsync(int id, CancellationToken ct = default);
    Task<UserDto> UpdateAsync(int id, UpdateUserRequest request, CancellationToken ct = default);
    Task ResetPasswordAsync(int id, ResetPasswordRequest request, CancellationToken ct = default);
    Task UnlockAsync(int id, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default);
}

public class UserService(
    IApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IAuditService audit,
    ICurrentUser currentUser,
    ICompanyClock clock) : IUserService
{
    public async Task<PagedResult<UserDto>> GetPagedAsync(UserQuery q, CancellationToken ct = default)
    {
        var query = db.Users.AsQueryable();
        if (q.IsActive is bool a) query = query.Where(u => u.IsActive == a);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            query = query.Where(u => u.FullName.ToLower().Contains(s) || u.Email!.ToLower().Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(q.Role))
        {
            var userIds = (await userManager.GetUsersInRoleAsync(q.Role)).Select(u => u.Id).ToList();
            query = query.Where(u => userIds.Contains(u.Id));
        }

        var total = await query.CountAsync(ct);
        var page = Math.Max(1, q.Page);
        query = q.SortBy?.ToLowerInvariant() switch
        {
            "email" => q.Descending ? query.OrderByDescending(u => u.Email) : query.OrderBy(u => u.Email),
            "lastloginat" => q.Descending ? query.OrderByDescending(u => u.LastLoginAt) : query.OrderBy(u => u.LastLoginAt),
            _ => q.Descending ? query.OrderByDescending(u => u.FullName) : query.OrderBy(u => u.FullName),
        };
        var users = await query.Skip((page - 1) * q.PageSize).Take(q.PageSize)
            .Select(u => new { User = u, EmpId = u.Employee != null ? (int?)u.Employee.Id : null, EmpCode = u.Employee != null ? u.Employee.EmployeeCode : null })
            .ToListAsync(ct);

        var items = new List<UserDto>();
        foreach (var x in users) items.Add(await ToDtoAsync(x.User, x.EmpId, x.EmpCode));
        return new PagedResult<UserDto>(items, total, page, q.PageSize);
    }

    public async Task<UserDto> GetAsync(int id, CancellationToken ct = default)
    {
        var x = await db.Users.Where(u => u.Id == id)
            .Select(u => new { User = u, EmpId = u.Employee != null ? (int?)u.Employee.Id : null, EmpCode = u.Employee != null ? u.Employee.EmployeeCode : null })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("User", id);
        return await ToDtoAsync(x.User, x.EmpId, x.EmpCode);
    }

    public async Task<UserDto> UpdateAsync(int id, UpdateUserRequest r, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User", id);
        if (id == currentUser.UserId && (!r.IsActive || !r.Roles.Contains(Roles.SuperAdmin)) && await userManager.IsInRoleAsync(user, Roles.SuperAdmin))
            throw new BadRequestException("You cannot remove your own Super Admin role or deactivate yourself.");

        var existingEmail = await userManager.FindByEmailAsync(r.Email);
        if (existingEmail is not null && existingEmail.Id != id) throw new ConflictException("Email already in use.");
        if (r.EmployeeId is int empForEmail && await db.Employees.AnyAsync(e => e.Email == r.Email.Trim() && e.Id != empForEmail && !e.IsDeleted, ct))
            throw new ConflictException("Another employee uses this email.");

        var oldRoles = (await userManager.GetRolesAsync(user)).ToArray();
        var old = new { user.FullName, user.Email, user.IsActive, Roles = oldRoles };

        user.FullName = r.FullName.Trim();
        user.Email = r.Email.Trim();
        user.UserName = r.Email.Trim();
        var wasActive = user.IsActive;
        user.IsActive = r.IsActive;
        var upd = await userManager.UpdateAsync(user);
        if (!upd.Succeeded) throw new BadRequestException(string.Join(" ", upd.Errors.Select(e => e.Description)));

        var toRemove = oldRoles.Except(r.Roles).ToArray();
        var toAdd = r.Roles.Distinct().Except(oldRoles).ToArray();
        if (toRemove.Length > 0) await userManager.RemoveFromRolesAsync(user, toRemove);
        if (toAdd.Length > 0) await userManager.AddToRolesAsync(user, toAdd);

        // Link / relink employee.
        var currentEmp = await db.Employees.FirstOrDefaultAsync(e => e.UserId == id, ct);
        if (currentEmp?.Id != r.EmployeeId)
        {
            if (currentEmp is not null) currentEmp.UserId = null;
            if (r.EmployeeId is int empId)
            {
                var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == empId && !e.IsDeleted, ct) ?? throw new NotFoundException("Employee", empId);
                if (emp.UserId is not null && emp.UserId != id) throw new ConflictException("That employee is linked to another account.");
                emp.UserId = id;
            }
        }

        // Keep the linked employee's email in step with the login email.
        if (r.EmployeeId is int linkedId)
        {
            var linked = await db.Employees.FirstOrDefaultAsync(e => e.Id == linkedId, ct);
            if (linked is not null && !string.Equals(linked.Email, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                linked.Email = user.Email!;
            }
        }

        if ((wasActive && !r.IsActive) || toRemove.Length > 0)
            await RevokeSessionsAsync(id, "Account changed by administrator", ct);

        audit.Log("Update", "User", id, old, new { user.FullName, user.Email, user.IsActive, Roles = r.Roles, r.EmployeeId });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task ResetPasswordAsync(int id, ResetPasswordRequest r, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User", id);
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var res = await userManager.ResetPasswordAsync(user, token, r.NewPassword);
        if (!res.Succeeded) throw new BadRequestException(string.Join(" ", res.Errors.Select(e => e.Description)));
        await RevokeSessionsAsync(id, "Password reset", ct);
        audit.Log("ResetPassword", "User", id);
        await db.SaveChangesAsync(ct);
    }

    public async Task UnlockAsync(int id, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User", id);
        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);
        await audit.LogNowAsync("Unlock", "User", id, ct: ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        if (id == currentUser.UserId) throw new BadRequestException("You cannot delete your own account.");
        var user = await userManager.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User", id);

        var hasHistory = await db.Tasks.AnyAsync(t => t.AssignedByUserId == id, ct)
                         || await db.TaskComments.AnyAsync(c => c.UserId == id, ct)
                         || await db.TaskAttachments.AnyAsync(a => a.UploadedByUserId == id, ct);
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.UserId == id, ct);

        if (hasHistory)
        {
            // Keep the row for traceability; disable it permanently.
            user.IsActive = false;
            user.LockoutEnd = DateTimeOffset.MaxValue;
            await userManager.UpdateAsync(user);
            await RevokeSessionsAsync(id, "Account deleted", ct);
            audit.Log("Deactivate", "User", id, reason: "Delete requested; user has task history so the account was disabled instead.");
            await db.SaveChangesAsync(ct);
            return;
        }

        if (emp is not null) emp.UserId = null;
        await db.Notifications.Where(n => n.UserId == id).ExecuteDeleteAsync(ct);
        await db.RefreshTokens.Where(t => t.UserId == id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        var res = await userManager.DeleteAsync(user);
        if (!res.Succeeded) throw new BadRequestException(string.Join(" ", res.Errors.Select(e => e.Description)));
        await audit.LogNowAsync("Delete", "User", id, new { user.Email, user.FullName }, ct: ct);
    }

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default)
    {
        var roles = await db.Roles.OrderBy(r => r.Id).ToListAsync(ct);
        var result = new List<RoleDto>();
        foreach (var r in roles)
        {
            var count = (await userManager.GetUsersInRoleAsync(r.Name!)).Count;
            result.Add(new RoleDto(r.Id, r.Name!, r.Description,
                Permissions.ByRole.TryGetValue(r.Name!, out var p) ? p : [], count));
        }
        return result;
    }

    private async Task RevokeSessionsAsync(int userId, string reason, CancellationToken ct)
    {
        var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) { t.RevokedAt = clock.UtcNow; t.RevokedReason = reason; }
    }

    private async Task<UserDto> ToDtoAsync(ApplicationUser u, int? empId, string? empCode)
    {
        var roles = (await userManager.GetRolesAsync(u)).ToArray();
        var locked = u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.UtcNow;
        return new UserDto(u.Id, u.FullName, u.Email ?? "", u.IsActive, roles, empId, empCode, u.LastLoginAt, locked, u.CreatedAt);
    }
}
