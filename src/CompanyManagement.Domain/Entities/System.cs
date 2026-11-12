using CompanyManagement.Domain.Common;
using CompanyManagement.Domain.Enums;

namespace CompanyManagement.Domain.Entities;

public class Notification : BaseEntity
{
    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? TitleAr { get; set; }
    public string? MessageAr { get; set; }
    /// <summary>Client-side route to open, e.g. "/tasks/12".</summary>
    public string? Link { get; set; }
    public string? RelatedEntityType { get; set; }
    public int? RelatedEntityId { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
}

public class AuditLog : ISourceTracked
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    public string? UserName { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? Reason { get; set; }
    public string? IpAddress { get; set; }
    /// <summary>Null for the TaskVeta app, "Claude" when the action was made through the MCP endpoint.</summary>
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; }
}
