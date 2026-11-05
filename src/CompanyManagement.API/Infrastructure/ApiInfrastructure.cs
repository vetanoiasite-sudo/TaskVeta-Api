using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Auth;
using CompanyManagement.Domain.Common;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.API.Infrastructure;

/// <summary>Maps application exceptions to RFC 7807 problem responses. Internal details are never leaked.</summary>
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title, detail) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "Validation failed", "One or more validation errors occurred."),
            BadRequestException e => (StatusCodes.Status400BadRequest, "Bad request", e.Message),
            DomainException e => (StatusCodes.Status400BadRequest, "Business rule violation", e.Message),
            UnauthorizedAppException e => (StatusCodes.Status401Unauthorized, "Unauthorized", e.Message),
            ForbiddenException e => (StatusCodes.Status403Forbidden, "Forbidden", e.Message),
            NotFoundException e => (StatusCodes.Status404NotFound, "Not found", e.Message),
            ConflictException e => (StatusCodes.Status409Conflict, "Conflict", e.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflict", "The record was modified by someone else. Reload and try again."),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "Client closed request", "The request was cancelled."),
            _ => (StatusCodes.Status500InternalServerError, "Server error", "An unexpected error occurred. Please try again later.")
        };

        if (status >= 500) logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
        else logger.LogInformation("Request {Method} {Path} failed with {Status}: {Message}", context.Request.Method, context.Request.Path, status, exception.Message);

        context.Response.StatusCode = status;
        var pd = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = context.Request.Path };
        if (exception is ValidationException ve)
            pd.Extensions["errors"] = ve.Errors.GroupBy(e => ToCamel(e.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
        pd.Extensions["traceId"] = context.TraceIdentifier;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = pd, Exception = exception });
    }

    private static string ToCamel(string s) => string.IsNullOrEmpty(s) ? "general" : char.ToLowerInvariant(s[0]) + s[1..];
}

/// <summary>Runs FluentValidation validators for every action argument that has one registered.</summary>
public class ValidationFilter(IServiceProvider services) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var arg in context.ActionArguments.Values)
        {
            if (arg is null) continue;
            var validatorType = typeof(IValidator<>).MakeGenericType(arg.GetType());
            if (services.GetService(validatorType) is not IValidator validator) continue;
            var result = await validator.ValidateAsync(new ValidationContext<object>(arg), context.HttpContext.RequestAborted);
            if (!result.IsValid) throw new ValidationException(result.Errors);
        }
        await next();
    }
}

public static class AuthorizationSetup
{
    /// <summary>One policy per permission, satisfied by any role that grants it.</summary>
    public static void AddPermissionPolicies(this AuthorizationOptions options)
    {
        foreach (var permission in Permissions.All)
        {
            var roles = Permissions.ByRole.Where(kv => kv.Value.Contains(permission)).Select(kv => kv.Key).ToArray();
            options.AddPolicy(permission, p => p.RequireAuthenticatedUser().RequireRole(roles));
        }
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
}

public static class ExportResults
{
    public static IActionResult File<T>(ControllerBase c, IExportService export, string format, string name, string title, string? subtitle,
        IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
        return format switch
        {
            "xlsx" => c.File(export.ToExcel(title, rows, columns), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}-{stamp}.xlsx"),
            "pdf" => c.File(export.ToPdf(title, subtitle, rows, columns), "application/pdf", $"{name}-{stamp}.pdf"),
            _ => throw new BadRequestException("Format must be xlsx or pdf.")
        };
    }
}

/// <summary>Cache headers for the Angular files served from wwwroot.</summary>
public static partial class SpaCaching
{
    // Angular output names look like main-ABCD1234.js / chunk-ABCD1234.js / styles-ABCD1234.css.
    [System.Text.RegularExpressions.GeneratedRegex(@"-[A-Z0-9]{8}\.(js|css|mjs)$")]
    private static partial System.Text.RegularExpressions.Regex HashedFile();

    public static void Apply(HttpContext context, string fileName) =>
        context.Response.Headers.CacheControl = HashedFile().IsMatch(fileName)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
}
