using CompanyManagement.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Audit;

public record AuditLogDto(long Id, int? UserId, string? UserName, string Action, string EntityName, string? EntityId,
    string? OldValues, string? NewValues, string? Reason, string? IpAddress, DateTime CreatedAt);

public class AuditQuery : PagedRequest
{
    public string? EntityName { get; set; }
    public string? EntityId { get; set; }
    public string? Action { get; set; }
    public int? UserId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public interface IAuditQueryService
{
    Task<PagedResult<AuditLogDto>> GetPagedAsync(AuditQuery q, CancellationToken ct = default);
}

public class AuditQueryService(IApplicationDbContext db) : IAuditQueryService
{
    public async Task<PagedResult<AuditLogDto>> GetPagedAsync(AuditQuery q, CancellationToken ct = default)
    {
        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.EntityName)) query = query.Where(a => a.EntityName == q.EntityName);
        if (!string.IsNullOrWhiteSpace(q.EntityId)) query = query.Where(a => a.EntityId == q.EntityId);
        if (!string.IsNullOrWhiteSpace(q.Action)) query = query.Where(a => a.Action == q.Action);
        if (q.UserId is int u) query = query.Where(a => a.UserId == u);
        if (q.From is DateTime f) query = query.Where(a => a.CreatedAt >= f);
        if (q.To is DateTime t) query = query.Where(a => a.CreatedAt <= t);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            query = query.Where(a => a.UserName!.ToLower().Contains(s) || a.Action.ToLower().Contains(s) || a.EntityName.ToLower().Contains(s) || a.Reason!.ToLower().Contains(s));
        }
        return await query.OrderByDescending(a => a.Id)
            .Select(a => new AuditLogDto(a.Id, a.UserId, a.UserName, a.Action, a.EntityName, a.EntityId, a.OldValues, a.NewValues, a.Reason, a.IpAddress, a.CreatedAt))
            .ToPagedAsync(q, ct);
    }
}
