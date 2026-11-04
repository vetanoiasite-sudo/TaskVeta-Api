using CompanyManagement.Application.Features.Auth;
using CompanyManagement.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CompanyManagement.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService auth, IConfiguration config) : ControllerBase
{
    public const string RefreshCookie = "cm_refresh";

    /// <summary>Authenticates a user. The refresh token is set as an HttpOnly cookie and never returned in the body.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request, ct);
        SetRefreshCookie(result.RefreshToken, result.RefreshTokenExpiresAt);
        return Ok(result.Response);
    }

    /// <summary>Rotates the refresh token (from the HttpOnly cookie) and issues a new access token.</summary>
    [HttpPost("refresh-token")]
    [AllowAnonymous]
    [EnableRateLimiting("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken ct)
    {
        var token = Request.Cookies[RefreshCookie];
        try
        {
            var result = await auth.RefreshAsync(token ?? string.Empty, ct);
            SetRefreshCookie(result.RefreshToken, result.RefreshTokenExpiresAt);
            return Ok(result.Response);
        }
        catch (UnauthorizedAppException)
        {
            ClearRefreshCookie();
            throw;
        }
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(Request.Cookies[RefreshCookie], ct);
        ClearRefreshCookie();
        return NoContent();
    }

    /// <summary>Creates a user account (Super Admin only). Self-registration is disabled for this internal system.</summary>
    [HttpPost("register")]
    [Authorize(Policy = Permissions.UsersManage)]
    public async Task<ActionResult<CurrentUserDto>> Register(RegisterRequest request, CancellationToken ct) =>
        Ok(await auth.RegisterAsync(request, ct));

    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserDto>> Me(CancellationToken ct) => Ok(await auth.GetCurrentUserAsync(ct));

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        await auth.ChangePasswordAsync(request, ct);
        ClearRefreshCookie();
        return NoContent();
    }

    public record LanguageRequest(string Language);

    [HttpPut("language")]
    public async Task<IActionResult> SetLanguage(LanguageRequest request, CancellationToken ct)
    {
        await auth.SetLanguageAsync(request.Language, ct);
        return NoContent();
    }

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = config.GetValue("Auth:SecureCookies", true),
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth",
        Expires = expires,
        IsEssential = true
    };

    private void SetRefreshCookie(string token, DateTime expiresAt) =>
        Response.Cookies.Append(RefreshCookie, token, CookieOptions(new DateTimeOffset(expiresAt, TimeSpan.Zero)));

    private void ClearRefreshCookie() => Response.Cookies.Delete(RefreshCookie, CookieOptions(null));
}
