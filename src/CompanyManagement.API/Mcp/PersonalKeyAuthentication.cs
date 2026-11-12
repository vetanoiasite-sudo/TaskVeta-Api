using System.Security.Claims;
using System.Text.Encodings.Web;
using CompanyManagement.Application.Features.Auth;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CompanyManagement.API.Mcp;

/// <summary>
/// Authenticates Claude with a personal key: <c>Authorization: Bearer tvk_…</c>. Used only by the <c>/mcp</c> endpoint, so a
/// leaked key can't reach the rest of the API. The user gets the same roles as at login, plus <c>via=Claude</c> so every change
/// is recorded as made through Claude.
/// </summary>
public class PersonalKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAuthService auth) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PersonalKey";
    public const string ClaudeSource = "Claude";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var key = header["Bearer ".Length..].Trim();
        if (!key.StartsWith(PersonalAccessToken.KeyPrefix, StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var user = await auth.AuthenticatePersonalKeyAsync(key, Context.RequestAborted);
        if (user is null) return AuthenticateResult.Fail("The personal key is invalid, expired or revoked.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
            new(AppClaims.Via, ClaudeSource),
        };
        if (user.EmployeeId is int e) claims.Add(new Claim(AppClaims.EmployeeId, e.ToString()));
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"TaskVeta\", error=\"invalid_token\"";
        return Response.WriteAsJsonAsync(new
        {
            error = "Create a personal key in TaskVeta (your name → Claude keys) and send it as: Authorization: Bearer tvk_…"
        });
    }
}
