using CompanyManagement.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CompanyManagement.Infrastructure.Realtime;

/// <summary>Real-time notification channel. Users are addressed by their NameIdentifier claim.</summary>
[Authorize]
public class NotificationHub : Hub
{
    public const string Path = "/hubs/notifications";
    public const string NotificationEvent = "notification";
}

public class SignalRNotifier(IHubContext<NotificationHub> hub) : IRealtimeNotifier
{
    public Task PushAsync(int userId, object payload, CancellationToken ct = default) =>
        hub.Clients.User(userId.ToString()).SendAsync(NotificationHub.NotificationEvent, payload, ct);
}
