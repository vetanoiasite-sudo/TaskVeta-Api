using CompanyManagement.API.Mcp;
using CompanyManagement.Domain.Security;
using CompanyManagement.Infrastructure.Realtime;
using CompanyManagement.Infrastructure.Services;

namespace CompanyManagement.API.Infrastructure;

/// <summary>
/// Narrows what some signed-in accounts may call, before endpoint authorization runs:
/// <list type="bullet">
/// <item>An account with a temporary password may only use the auth endpoints until it sets its own password.</item>
/// <item>Client accounts only reach the client portal API, their notifications and the auth endpoints — never the internal API.</item>
/// </list>
/// </summary>
public static class AccountGate
{
    private static readonly PathString[] ServerPaths = ["/api", NotificationHub.Path, McpSetup.Path];
    private static readonly PathString[] ClientPaths = ["/api/auth", "/api/portal", "/api/notifications", NotificationHub.Path];

    public static IApplicationBuilder UseAccountGate(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var user = ctx.User;
        var path = ctx.Request.Path;
        if (user.Identity?.IsAuthenticated == true && ServerPaths.Any(p => path.StartsWithSegments(p)))
        {
            if (user.HasClaim(AppClaims.MustChangePassword, "1") && !path.StartsWithSegments("/api/auth"))
            {
                await Deny(ctx, "Change your temporary password first.");
                return;
            }
            if (user.IsInRole(Roles.Client) && !ClientPaths.Any(p => path.StartsWithSegments(p)))
            {
                await Deny(ctx, "Client accounts can only use the client portal.");
                return;
            }
        }
        await next();
    });

    private static Task Deny(HttpContext ctx, string detail)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return ctx.Response.WriteAsJsonAsync(new { title = "Forbidden", status = 403, detail }, (System.Text.Json.JsonSerializerOptions?)null,
            "application/problem+json");
    }
}
