using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Auth;

public record LoginRequest(string Email, string Password);
public record RegisterRequest(string Email, string FullName, string Password, string Role, int? EmployeeId);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record CurrentUserDto(int Id, string FullName, string Email, string[] Roles, string[] Permissions,
    int? EmployeeId, string PreferredLanguage);

public record AuthResponse(string AccessToken, DateTime ExpiresAt, CurrentUserDto User);

/// <summary>Internal result; the refresh token is delivered to the client only via an HttpOnly cookie.</summary>
public record AuthResult(AuthResponse Response, string RefreshToken, DateTime RefreshTokenExpiresAt);

public class UnauthorizedAppException(string message) : Exception(message);

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
        RuleFor(x => x.Role).NotEmpty().Must(r => Roles.All.Contains(r)).WithMessage("Unknown role.");
    }
}

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128).NotEqual(x => x.CurrentPassword);
    }
}

public interface IAuthService
{
    Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task LogoutAsync(string? refreshToken, CancellationToken ct = default);
    Task<CurrentUserDto> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<CurrentUserDto> GetCurrentUserAsync(CancellationToken ct = default);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);
    Task SetLanguageAsync(string language, CancellationToken ct = default);
}

public class AuthService(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    ITokenService tokens,
    ICurrentUser currentUser,
    IAuditService audit,
    ICompanyClock clock) : IAuthService
{
    private const string InvalidCredentials = "Invalid email or password.";

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(request.Email) ?? await userManager.FindByNameAsync(request.Email);
        if (user is null)
        {
            await audit.LogNowAsync("LoginFailed", "User", null, new { request.Email }, "Unknown user", ct);
            throw new UnauthorizedAppException(InvalidCredentials);
        }

        if (!user.IsActive)
        {
            await audit.LogNowAsync("LoginFailed", "User", user.Id, null, "Account deactivated", ct);
            throw new UnauthorizedAppException("This account is deactivated.");
        }

        if (await userManager.IsLockedOutAsync(user))
            throw new UnauthorizedAppException("Account temporarily locked due to failed login attempts. Try again later.");

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            await userManager.AccessFailedAsync(user);
            await audit.LogNowAsync("LoginFailed", "User", user.Id, null, "Wrong password", ct);
            throw new UnauthorizedAppException(InvalidCredentials);
        }

        await userManager.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = clock.UtcNow;
        await userManager.UpdateAsync(user);

        var result = await IssueTokensAsync(user, Guid.NewGuid(), ct);
        await audit.LogNowAsync("Login", "User", user.Id, null, null, ct);
        return result;
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new UnauthorizedAppException("Missing refresh token.");

        var hash = tokens.HashToken(refreshToken);
        var existing = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
                       ?? throw new UnauthorizedAppException("Invalid refresh token.");
        var now = clock.UtcNow;

        if (existing.RevokedAt is not null)
        {
            // Reuse of a rotated/revoked token: assume theft and revoke the whole family.
            var family = await db.RefreshTokens
                .Where(t => t.FamilyId == existing.FamilyId && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in family) { t.RevokedAt = now; t.RevokedReason = "Reuse detected"; }
            audit.Log("RefreshTokenReuse", "User", existing.UserId, reason: "Revoked refresh token was presented; token family revoked.");
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedAppException("Invalid refresh token.");
        }

        if (existing.ExpiresAt <= now) throw new UnauthorizedAppException("Refresh token expired.");
        if (!existing.User.IsActive) throw new UnauthorizedAppException("This account is deactivated.");

        var result = await IssueTokensAsync(existing.User, existing.FamilyId, ct, existing);
        return result;
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        var hash = tokens.HashToken(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return;
        var family = await db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in family) { t.RevokedAt = clock.UtcNow; t.RevokedReason = "Logout"; }
        audit.Log("Logout", "User", token.UserId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<CurrentUserDto> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        if (await userManager.FindByEmailAsync(request.Email) is not null)
            throw new ConflictException("A user with this email already exists.");

        Employee? employee = null;
        if (request.EmployeeId is int empId)
        {
            employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == empId && !e.IsDeleted, ct)
                       ?? throw new NotFoundException("Employee", empId);
            if (employee.UserId is not null) throw new ConflictException("This employee already has a user account.");
        }

        var user = new ApplicationUser
        {
            UserName = request.Email, Email = request.Email, FullName = request.FullName,
            EmailConfirmed = true, IsActive = true, LockoutEnabled = true
        };
        var create = await userManager.CreateAsync(user, request.Password);
        if (!create.Succeeded) throw new BadRequestException(string.Join(" ", create.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, request.Role);

        if (employee is not null)
        {
            employee.UserId = user.Id;
        }
        audit.Log("Register", "User", user.Id, newValues: new { user.Email, user.FullName, request.Role, request.EmployeeId });
        await db.SaveChangesAsync(ct);
        return await BuildUserDtoAsync(user, ct);
    }

    public async Task<CurrentUserDto> GetCurrentUserAsync(CancellationToken ct = default)
    {
        var user = await RequireCurrentUserAsync();
        return await BuildUserDtoAsync(user, ct);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await RequireCurrentUserAsync();
        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded) throw new BadRequestException(string.Join(" ", result.Errors.Select(e => e.Description)));
        // Invalidate all sessions after a password change.
        var active = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active) { t.RevokedAt = clock.UtcNow; t.RevokedReason = "Password changed"; }
        audit.Log("PasswordChanged", "User", user.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetLanguageAsync(string language, CancellationToken ct = default)
    {
        if (language is not ("en" or "ar")) throw new BadRequestException("Supported languages: en, ar.");
        var user = await RequireCurrentUserAsync();
        user.PreferredLanguage = language;
        await userManager.UpdateAsync(user);
    }

    private async Task<ApplicationUser> RequireCurrentUserAsync()
    {
        if (currentUser.UserId is not int id) throw new UnauthorizedAppException("Not authenticated.");
        return await userManager.FindByIdAsync(id.ToString()) ?? throw new UnauthorizedAppException("User not found.");
    }

    private async Task<AuthResult> IssueTokensAsync(ApplicationUser user, Guid familyId, CancellationToken ct, RefreshToken? replacing = null)
    {
        var employeeId = await db.Employees.Where(e => e.UserId == user.Id && !e.IsDeleted).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        var roles = await EffectiveRolesAsync(user, employeeId, ct);
        var access = tokens.CreateAccessToken(user, roles, employeeId, out var expiresAt);

        var raw = tokens.GenerateRefreshToken();
        var now = clock.UtcNow;
        var entity = new RefreshToken
        {
            UserId = user.Id, TokenHash = tokens.HashToken(raw), FamilyId = familyId,
            ExpiresAt = now.Add(tokens.RefreshTokenLifetime), CreatedByIp = currentUser.IpAddress
        };
        db.RefreshTokens.Add(entity);
        if (replacing is not null)
        {
            replacing.RevokedAt = now;
            replacing.RevokedReason = "Rotated";
            replacing.ReplacedByTokenHash = entity.TokenHash;
        }

        // Housekeeping: drop this user's long-expired tokens.
        var cutoff = now.AddDays(-30);
        var stale = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.ExpiresAt < cutoff).ToListAsync(ct);
        db.RefreshTokens.RemoveRange(stale);

        await db.SaveChangesAsync(ct);

        var dto = ToDto(user, roles, employeeId);
        return new AuthResult(new AuthResponse(access, expiresAt, dto), raw, entity.ExpiresAt);
    }

    private async Task<CurrentUserDto> BuildUserDtoAsync(ApplicationUser user, CancellationToken ct)
    {
        var employeeId = await db.Employees.Where(e => e.UserId == user.Id && !e.IsDeleted).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        var roles = await EffectiveRolesAsync(user, employeeId, ct);
        return ToDto(user, roles, employeeId);
    }

    /// <summary>Identity roles plus <see cref="Roles.Leader"/> when the employee heads a department or leads a team.</summary>
    private async Task<string[]> EffectiveRolesAsync(ApplicationUser user, int? employeeId, CancellationToken ct)
    {
        var roles = (await userManager.GetRolesAsync(user)).ToList();
        if (employeeId is int me && (
                await db.Departments.AnyAsync(d => d.ManagerEmployeeId == me && d.IsActive, ct) ||
                await db.Teams.AnyAsync(t => t.LeaderEmployeeId == me && t.IsActive, ct)))
            roles.Add(Roles.Leader);
        return roles.ToArray();
    }

    private static CurrentUserDto ToDto(ApplicationUser user, string[] roles, int? employeeId) =>
        new(user.Id, user.FullName, user.Email ?? string.Empty, roles, Permissions.ForRoles(roles).ToArray(),
            employeeId, user.PreferredLanguage);
}
