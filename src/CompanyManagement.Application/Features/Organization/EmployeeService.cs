using System.Linq.Expressions;
using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Organization;

public record EmployeeTeamDto(int Id, string Name);

public record EmployeeDto(int Id, string EmployeeCode, string FirstName, string LastName, string FullName, string Email,
    string? Phone, string? JobTitle, DateOnly HireDate, bool IsActive, bool AllowRemoteCheckIn, int DepartmentId,
    string DepartmentName, int? WorkScheduleId, string? WorkScheduleName, int? UserId, IReadOnlyList<EmployeeTeamDto> Teams,
    DateTime CreatedAt);

public class EmployeeQuery : PagedRequest
{
    public int? DepartmentId { get; set; }
    public int? TeamId { get; set; }
    public bool? IsActive { get; set; }
}

public record CreateEmployeeRequest(string EmployeeCode, string FirstName, string LastName, string Email, string? Phone,
    string? JobTitle, DateOnly HireDate, int DepartmentId, int? WorkScheduleId, int[]? TeamIds, bool AllowRemoteCheckIn,
    bool CreateAccount, string? Password, string? Role);

public record UpdateEmployeeRequest(string EmployeeCode, string FirstName, string LastName, string Email, string? Phone,
    string? JobTitle, DateOnly HireDate, int DepartmentId, int? WorkScheduleId, int[]? TeamIds, bool AllowRemoteCheckIn,
    bool IsActive);

public class CreateEmployeeRequestValidator : AbstractValidator<CreateEmployeeRequest>
{
    public CreateEmployeeRequestValidator()
    {
        RuleFor(x => x.EmployeeCode).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9_-]+$");
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(75);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(75);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.JobTitle).MaximumLength(100);
        RuleFor(x => x.DepartmentId).GreaterThan(0);
        When(x => x.CreateAccount, () =>
        {
            RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
            RuleFor(x => x.Role).Must(r => r is null || Roles.All.Contains(r)).WithMessage("Unknown role.");
        });
    }
}

public class UpdateEmployeeRequestValidator : AbstractValidator<UpdateEmployeeRequest>
{
    public UpdateEmployeeRequestValidator()
    {
        RuleFor(x => x.EmployeeCode).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9_-]+$");
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(75);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(75);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.JobTitle).MaximumLength(100);
        RuleFor(x => x.DepartmentId).GreaterThan(0);
    }
}

public interface IEmployeeService
{
    Task<PagedResult<EmployeeDto>> GetPagedAsync(EmployeeQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<LookupDto>> LookupAsync(int? departmentId, int? teamId, CancellationToken ct = default);
    Task<EmployeeDto> GetAsync(int id, CancellationToken ct = default);
    Task<EmployeeDto> CreateAsync(CreateEmployeeRequest request, CancellationToken ct = default);
    Task<EmployeeDto> UpdateAsync(int id, UpdateEmployeeRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public class EmployeeService(
    IApplicationDbContext db,
    IAccessScope scope,
    IAuditService audit,
    ICurrentUser currentUser,
    UserManager<ApplicationUser> userManager,
    ICompanyClock clock) : IEmployeeService
{
    private static readonly Dictionary<string, Expression<Func<Employee, object?>>> Sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["employeeCode"] = e => e.EmployeeCode,
        ["fullName"] = e => e.FirstName,
        ["email"] = e => e.Email,
        ["departmentName"] = e => e.Department.Name,
        ["jobTitle"] = e => e.JobTitle,
        ["hireDate"] = e => e.HireDate,
        ["isActive"] = e => e.IsActive,
    };

    internal static readonly Expression<Func<Employee, EmployeeDto>> Projection = e => new EmployeeDto(
        e.Id, e.EmployeeCode, e.FirstName, e.LastName, e.FirstName + " " + e.LastName, e.Email, e.Phone, e.JobTitle,
        e.HireDate, e.IsActive, e.AllowRemoteCheckIn, e.DepartmentId, e.Department.Name, e.WorkScheduleId,
        e.WorkSchedule != null ? e.WorkSchedule.Name : null, e.UserId,
        e.TeamMemberships.Select(m => new EmployeeTeamDto(m.TeamId, m.Team.Name)).ToList(), e.CreatedAt);

    public async Task<PagedResult<EmployeeDto>> GetPagedAsync(EmployeeQuery q, CancellationToken ct = default)
    {
        var query = await scope.ScopedEmployeesAsync(ct);
        if (q.DepartmentId is int d) query = query.Where(e => e.DepartmentId == d);
        if (q.TeamId is int t) query = query.Where(e => e.TeamMemberships.Any(m => m.TeamId == t));
        if (q.IsActive is bool a) query = query.Where(e => e.IsActive == a);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(e => e.FirstName.Contains(s) || e.LastName.Contains(s) || e.EmployeeCode.Contains(s)
                                     || e.Email.Contains(s) || (e.FirstName + " " + e.LastName).Contains(s));
        }
        return await query.ApplySort(q, Sorts, "employeeCode").Select(Projection).ToPagedAsync(q, ct);
    }

    public async Task<IReadOnlyList<LookupDto>> LookupAsync(int? departmentId, int? teamId, CancellationToken ct = default)
    {
        var query = (await scope.ScopedEmployeesAsync(ct)).Where(e => e.IsActive);
        if (departmentId is int d) query = query.Where(e => e.DepartmentId == d);
        if (teamId is int t) query = query.Where(e => e.TeamMemberships.Any(m => m.TeamId == t));
        return await query.OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new LookupDto(e.Id, e.FirstName + " " + e.LastName, e.EmployeeCode)).ToListAsync(ct);
    }

    public async Task<EmployeeDto> GetAsync(int id, CancellationToken ct = default)
    {
        await scope.EnsureCanAccessEmployeeAsync(id, ct);
        return await db.Employees.Where(e => e.Id == id && !e.IsDeleted).Select(Projection).FirstOrDefaultAsync(ct)
               ?? throw new NotFoundException("Employee", id);
    }

    public async Task<EmployeeDto> CreateAsync(CreateEmployeeRequest r, CancellationToken ct = default)
    {
        await ValidateAsync(null, r.EmployeeCode, r.Email, r.DepartmentId, r.WorkScheduleId, r.TeamIds, ct);

        var role = r.Role ?? Roles.Employee;
        if (r.CreateAccount && role != Roles.Employee && !currentUser.HasPermission(Permissions.RolesManage))
            throw new ForbiddenException("Only a Super Admin can create accounts with elevated roles.");

        return await db.ExecuteInTransactionAsync(async () =>
        {
            var e = new Employee
            {
                EmployeeCode = r.EmployeeCode.Trim().ToUpperInvariant(), FirstName = r.FirstName.Trim(), LastName = r.LastName.Trim(),
                Email = r.Email.Trim(), Phone = r.Phone, JobTitle = r.JobTitle, HireDate = r.HireDate, DepartmentId = r.DepartmentId,
                WorkScheduleId = r.WorkScheduleId, AllowRemoteCheckIn = r.AllowRemoteCheckIn, IsActive = true
            };

            if (r.CreateAccount)
            {
                if (await userManager.FindByEmailAsync(e.Email) is not null)
                    throw new ConflictException("A user account with this email already exists.");
                var user = new ApplicationUser
                {
                    UserName = e.Email, Email = e.Email, FullName = e.FullName, EmailConfirmed = true, IsActive = true, LockoutEnabled = true
                };
                var res = await userManager.CreateAsync(user, r.Password!);
                if (!res.Succeeded) throw new BadRequestException(string.Join(" ", res.Errors.Select(x => x.Description)));
                await userManager.AddToRoleAsync(user, role);
                e.UserId = user.Id;
            }

            db.Employees.Add(e);
            await db.SaveChangesAsync(ct);

            var today = await clock.TodayAsync(ct);
            foreach (var teamId in (r.TeamIds ?? []).Distinct())
                db.TeamMembers.Add(new TeamMember { TeamId = teamId, EmployeeId = e.Id, JoinedOn = today });

            audit.Log("Create", nameof(Employee), e.Id, newValues: new
            {
                e.EmployeeCode, e.FirstName, e.LastName, e.Email, e.DepartmentId, r.TeamIds, r.CreateAccount, Role = r.CreateAccount ? role : null
            });
            await db.SaveChangesAsync(ct);
            return await db.Employees.Where(x => x.Id == e.Id).Select(Projection).FirstAsync(ct);
        }, ct);
    }

    public async Task<EmployeeDto> UpdateAsync(int id, UpdateEmployeeRequest r, CancellationToken ct = default)
    {
        var e = await db.Employees.Include(x => x.TeamMemberships).FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct)
                ?? throw new NotFoundException("Employee", id);
        await ValidateAsync(id, r.EmployeeCode, r.Email, r.DepartmentId, r.WorkScheduleId, r.TeamIds, ct);

        var old = new { e.EmployeeCode, e.FirstName, e.LastName, e.Email, e.DepartmentId, e.WorkScheduleId, e.IsActive, e.AllowRemoteCheckIn };
        e.EmployeeCode = r.EmployeeCode.Trim().ToUpperInvariant();
        e.FirstName = r.FirstName.Trim();
        e.LastName = r.LastName.Trim();
        e.Email = r.Email.Trim();
        e.Phone = r.Phone;
        e.JobTitle = r.JobTitle;
        e.HireDate = r.HireDate;
        e.DepartmentId = r.DepartmentId;
        e.WorkScheduleId = r.WorkScheduleId;
        e.AllowRemoteCheckIn = r.AllowRemoteCheckIn;
        e.IsActive = r.IsActive;

        if (r.TeamIds is not null)
        {
            var desired = r.TeamIds.ToHashSet();
            db.TeamMembers.RemoveRange(e.TeamMemberships.Where(m => !desired.Contains(m.TeamId)));
            var today = await clock.TodayAsync(ct);
            foreach (var t in desired.Except(e.TeamMemberships.Select(m => m.TeamId)))
                db.TeamMembers.Add(new TeamMember { TeamId = t, EmployeeId = id, JoinedOn = today });
        }

        if (e.UserId is int uid)
        {
            var user = await userManager.FindByIdAsync(uid.ToString());
            if (user is not null)
            {
                user.FullName = e.FullName;
                if (!r.IsActive && user.IsActive) user.IsActive = false;
                await userManager.UpdateAsync(user);
            }
        }

        audit.Log("Update", nameof(Employee), id, old, new
        {
            e.EmployeeCode, e.FirstName, e.LastName, e.Email, e.DepartmentId, e.WorkScheduleId, e.IsActive, e.AllowRemoteCheckIn, r.TeamIds
        });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Soft delete: history (attendance, tasks) is preserved and the linked account is disabled.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct) ?? throw new NotFoundException("Employee", id);
        e.IsDeleted = true;
        e.IsActive = false;
        if (e.UserId is int uid)
        {
            var user = await userManager.FindByIdAsync(uid.ToString());
            if (user is not null)
            {
                user.IsActive = false;
                await userManager.UpdateAsync(user);
                var tokens = await db.RefreshTokens.Where(t => t.UserId == uid && t.RevokedAt == null).ToListAsync(ct);
                foreach (var t in tokens) { t.RevokedAt = clock.UtcNow; t.RevokedReason = "Employee deleted"; }
            }
        }
        // Detach from management positions.
        await db.Departments.Where(d => d.ManagerEmployeeId == id).ForEachAsync(d => d.ManagerEmployeeId = null, ct);
        await db.Teams.Where(t => t.LeaderEmployeeId == id).ForEachAsync(t => t.LeaderEmployeeId = null, ct);
        audit.Log("Delete", nameof(Employee), id, new { e.EmployeeCode, e.FirstName, e.LastName });
        await db.SaveChangesAsync(ct);
    }

    private async Task ValidateAsync(int? id, string code, string email, int departmentId, int? scheduleId, int[]? teamIds, CancellationToken ct)
    {
        var normCode = code.Trim().ToUpperInvariant();
        if (await db.Employees.AnyAsync(e => e.EmployeeCode == normCode && e.Id != id, ct))
            throw new ConflictException($"Employee code '{normCode}' is already used.");
        if (await db.Employees.AnyAsync(e => e.Email == email.Trim() && e.Id != id && !e.IsDeleted, ct))
            throw new ConflictException("Another employee uses this email.");
        if (!await db.Departments.AnyAsync(d => d.Id == departmentId, ct)) throw new NotFoundException("Department", departmentId);
        if (scheduleId is int s && !await db.WorkSchedules.AnyAsync(w => w.Id == s, ct)) throw new NotFoundException("WorkSchedule", s);
        if (teamIds is { Length: > 0 })
        {
            var distinct = teamIds.Distinct().ToArray();
            var found = await db.Teams.CountAsync(t => distinct.Contains(t.Id), ct);
            if (found != distinct.Length) throw new BadRequestException("One or more teams do not exist.");
        }
    }
}
