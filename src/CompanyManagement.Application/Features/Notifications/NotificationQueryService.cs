using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Notifications;

public record NotificationDto(int Id, NotificationType Type, string Title, string Message, string? TitleAr, string? MessageAr,
    string? Link, string? RelatedEntityType, int? RelatedEntityId, bool IsRead, DateTime? ReadAt, DateTime CreatedAt);

public class NotificationQuery : PagedRequest
{
    public bool? UnreadOnly { get; set; }
    public NotificationType? Type { get; set; }
}

public interface INotificationQueryService
{
    Task<PagedResult<NotificationDto>> GetMineAsync(NotificationQuery q, CancellationToken ct = default);
    Task<int> UnreadCountAsync(CancellationToken ct = default);
    Task MarkReadAsync(int id, CancellationToken ct = default);
    Task MarkAllReadAsync(CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public class NotificationQueryService(IApplicationDbContext db, ICurrentUser user, ICompanyClock clock) : INotificationQueryService
{
    private int Me => user.UserId ?? throw new ForbiddenException();

    public async Task<PagedResult<NotificationDto>> GetMineAsync(NotificationQuery q, CancellationToken ct = default)
    {
        var me = Me;
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == me);
        if (q.UnreadOnly == true) query = query.Where(n => !n.IsRead);
        if (q.Type is NotificationType t) query = query.Where(n => n.Type == t);
        return await query.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Message, n.TitleAr, n.MessageAr, n.Link, n.RelatedEntityType,
                n.RelatedEntityId, n.IsRead, n.ReadAt, n.CreatedAt))
            .ToPagedAsync(q, ct);
    }

    public Task<int> UnreadCountAsync(CancellationToken ct = default)
    {
        var me = Me;
        return db.Notifications.CountAsync(n => n.UserId == me && !n.IsRead, ct);
    }

    public async Task MarkReadAsync(int id, CancellationToken ct = default)
    {
        var me = Me;
        var now = clock.UtcNow;
        var count = await db.Notifications.Where(n => n.Id == id && n.UserId == me && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true).SetProperty(n => n.ReadAt, now), ct);
        if (count == 0 && !await db.Notifications.AnyAsync(n => n.Id == id && n.UserId == me, ct))
            throw new NotFoundException("Notification", id);
    }

    public async Task MarkAllReadAsync(CancellationToken ct = default)
    {
        var me = Me;
        var now = clock.UtcNow;
        await db.Notifications.Where(n => n.UserId == me && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true).SetProperty(n => n.ReadAt, now), ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var me = Me;
        var count = await db.Notifications.Where(n => n.Id == id && n.UserId == me).ExecuteDeleteAsync(ct);
        if (count == 0) throw new NotFoundException("Notification", id);
    }
}
