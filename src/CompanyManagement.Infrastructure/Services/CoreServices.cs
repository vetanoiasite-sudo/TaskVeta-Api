using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using CompanyManagement.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CompanyManagement.Infrastructure.Services;

public static class AppClaims
{
    public const string EmployeeId = "employee_id";
    public const string Permission = "permission";
    /// <summary>Set to "Claude" on requests authenticated with a personal key (the MCP endpoint).</summary>
    public const string Via = "via";
}

public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public int? UserId => int.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? UserName => Principal?.FindFirstValue(ClaimTypes.Name) ?? Principal?.FindFirstValue(ClaimTypes.Email);
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public IReadOnlyCollection<string> Roles => Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray() ?? [];
    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;
    public bool HasPermission(string permission) => Permissions.ForRoles(Roles).Contains(permission);
    public string? Via => Principal?.FindFirstValue(AppClaims.Via);
}

public class CompanyClock(TimeProvider time, IServiceProvider services, IMemoryCache cache) : ICompanyClock
{
    private const string CacheKey = "company-timezone";

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public async Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out TimeZoneInfo? tz) && tz is not null) return tz;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetService<AppDbContext>();
        var id = db is null ? null : await db.CompanySettings.AsNoTracking().OrderBy(s => s.Id).Select(s => s.TimeZoneId).FirstOrDefaultAsync(ct);
        tz = id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found) ? found : TimeZoneInfo.Local;
        CompanyTimeZone.Current = tz; // used when reading attendance times from the database
        cache.Set(CacheKey, tz, TimeSpan.FromMinutes(10));
        return tz;
    }

    public async Task<DateTimeOffset> NowAsync(CancellationToken ct = default) => ToCompanyTime(UtcNow, await GetTimeZoneAsync(ct));

    public async Task<DateOnly> TodayAsync(CancellationToken ct = default) => DateOnly.FromDateTime((await NowAsync(ct)).DateTime);

    public DateTimeOffset ToCompanyTime(DateTime utc, TimeZoneInfo tz)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(u, tz);
        return new DateTimeOffset(local, tz.GetUtcOffset(u));
    }

    public DateTimeOffset ToCompanyTime(DateOnly date, TimeOnly t, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(t), DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1); // DST gap
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    public void InvalidateCache() => cache.Remove(CacheKey);
}

public class AuditService(AppDbContext db, ICurrentUser user, TimeProvider time) : IAuditService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] SensitiveKeys = ["password", "token", "secret", "refresh"];

    public void Log(string action, string entityName, object? entityId, object? oldValues = null, object? newValues = null, string? reason = null) =>
        db.AuditLogs.Add(Build(action, entityName, entityId, oldValues, newValues, reason));

    public async Task LogNowAsync(string action, string entityName, object? entityId, object? newValues = null, string? reason = null, CancellationToken ct = default)
    {
        db.AuditLogs.Add(Build(action, entityName, entityId, null, newValues, reason));
        await db.SaveChangesAsync(ct);
    }

    private AuditLog Build(string action, string entityName, object? entityId, object? oldValues, object? newValues, string? reason) => new()
    {
        UserId = user.UserId, UserName = user.UserName, Action = action, EntityName = entityName, EntityId = entityId?.ToString(),
        OldValues = Serialize(oldValues), NewValues = Serialize(newValues), Reason = reason?.Length > 1000 ? reason[..1000] : reason,
        IpAddress = user.IpAddress, CreatedAt = time.GetUtcNow().UtcDateTime
    };

    /// <summary>Serializes audit payloads, stripping any property whose name suggests a secret.</summary>
    private static string? Serialize(object? value)
    {
        if (value is null) return null;
        var element = JsonSerializer.SerializeToElement(value, Json);
        if (element.ValueKind != JsonValueKind.Object) return element.GetRawText();
        var dict = element.EnumerateObject()
            .Where(p => !SensitiveKeys.Any(k => p.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(p => p.Name, p => p.Value);
        return JsonSerializer.Serialize(dict, Json);
    }
}

public class NotificationService(AppDbContext db, IRealtimeNotifier realtime, ILogger<NotificationService> logger) : INotificationService
{
    private readonly List<Notification> _pending = [];

    public void Add(int userId, NotificationType type, string title, string message, string? titleAr = null, string? messageAr = null,
        string? link = null, string? entityType = null, int? entityId = null)
    {
        if (_pending.Any(n => n.UserId == userId && n.Type == type && n.RelatedEntityId == entityId && n.RelatedEntityType == entityType && n.Message == message))
            return;
        var n = new Notification
        {
            UserId = userId, Type = type, Title = Trim(title, 200), Message = Trim(message, 1000), TitleAr = titleAr is null ? null : Trim(titleAr, 200),
            MessageAr = messageAr is null ? null : Trim(messageAr, 1000), Link = link, RelatedEntityType = entityType, RelatedEntityId = entityId
        };
        db.Notifications.Add(n);
        _pending.Add(n);
    }

    public void AddForEmployees(IEnumerable<int> employeeIds, NotificationType type, string title, string message, string? titleAr = null,
        string? messageAr = null, string? link = null, string? entityType = null, int? entityId = null)
    {
        var ids = employeeIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var userIds = db.Employees.Where(e => ids.Contains(e.Id) && e.UserId != null && !e.IsDeleted).Select(e => e.UserId!.Value).ToList();
        foreach (var uid in userIds) Add(uid, type, title, message, titleAr, messageAr, link, entityType, entityId);
    }

    public async Task SaveAndDispatchAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        var toSend = _pending.ToList();
        _pending.Clear();
        foreach (var n in toSend)
        {
            try
            {
                await realtime.PushAsync(n.UserId, new
                {
                    n.Id, n.Type, n.Title, n.Message, n.TitleAr, n.MessageAr, n.Link, n.RelatedEntityType, n.RelatedEntityId,
                    n.IsRead, n.ReadAt, n.CreatedAt
                }, ct);
            }
            catch (Exception ex)
            {
                // Persisted already; real-time delivery is best effort.
                logger.LogWarning(ex, "Failed to push notification {NotificationId} to user {UserId}", n.Id, n.UserId);
            }
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

