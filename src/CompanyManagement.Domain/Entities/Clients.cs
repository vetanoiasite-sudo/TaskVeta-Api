using CompanyManagement.Domain.Common;
using CompanyManagement.Domain.Enums;

namespace CompanyManagement.Domain.Entities;

/// <summary>
/// A customer of the company. Has one login (<see cref="UserId"/>, role Client) created with a temporary password; the client
/// changes it on first sign-in and then opens support tickets and sees the projects linked to them.
/// </summary>
public class Client : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    /// <summary>The person we deal with at the client.</summary>
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    /// <summary>Internal notes about the client (never shown to the client).</summary>
    public string? Notes { get; set; }

    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public int? CreatedByUserId { get; set; }

    public ICollection<ClientProject> Projects { get; set; } = new List<ClientProject>();
    public ICollection<SupportTicket> Tickets { get; set; } = new List<SupportTicket>();
}

/// <summary>A company project delivered to a client, with notes on what was customised or added for that client.</summary>
public class ClientProject : BaseEntity
{
    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public ICollection<ClientProjectNote> Notes { get; set; } = new List<ClientProjectNote>();
}

/// <summary>A note on a client's copy of a project: an improvement or an addition made for that client.</summary>
public class ClientProjectNote : BaseEntity
{
    public int ClientProjectId { get; set; }
    public ClientProject ClientProject { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    /// <summary>Shown to the client in their portal; otherwise internal.</summary>
    public bool VisibleToClient { get; set; }
    public int CreatedByUserId { get; set; }
    public ApplicationUser CreatedBy { get; set; } = null!;
}

/// <summary>A support request opened by a client.</summary>
public class SupportTicket : BaseEntity
{
    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;
    /// <summary>One of the client's projects, when the problem is about one.</summary>
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TicketCategory Category { get; set; } = TicketCategory.Other;
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    public TicketStatus Status { get; set; } = TicketStatus.Open;

    public int CreatedByUserId { get; set; }
    public ApplicationUser CreatedBy { get; set; } = null!;
    /// <summary>The staff member handling the ticket.</summary>
    public int? AssignedToUserId { get; set; }
    public ApplicationUser? AssignedTo { get; set; }

    public DateTime LastActivityAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    public ICollection<TicketMessage> Messages { get; set; } = new List<TicketMessage>();
    public ICollection<TicketAttachment> Attachments { get; set; } = new List<TicketAttachment>();
}

/// <summary>A comment on a ticket. Internal notes are only seen by staff.</summary>
public class TicketMessage : BaseEntity
{
    public int TicketId { get; set; }
    public SupportTicket Ticket { get; set; } = null!;
    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    public bool IsInternal { get; set; }

    public ICollection<TicketAttachment> Attachments { get; set; } = new List<TicketAttachment>();
}

/// <summary>A file, image or voice note on a ticket (sent with a message, or with the ticket itself when MessageId is null).</summary>
public class TicketAttachment : BaseEntity
{
    public int TicketId { get; set; }
    public SupportTicket Ticket { get; set; } = null!;
    public int? MessageId { get; set; }
    public TicketMessage? Message { get; set; }
    public string FileName { get; set; } = string.Empty;
    /// <summary>Random server-side name; files are stored outside the web root.</summary>
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    /// <summary>Length of a voice recording.</summary>
    public int? DurationSeconds { get; set; }
    public int UploadedByUserId { get; set; }
    public ApplicationUser UploadedBy { get; set; } = null!;
}

/// <summary>A company template (BR, discovery, design, test plan…) that people download to start a project document.</summary>
public class CompanyTemplate : BaseEntity
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectDocumentCategory Category { get; set; } = ProjectDocumentCategory.Other;
    public string FileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public int UploadedByUserId { get; set; }
    public ApplicationUser UploadedBy { get; set; } = null!;
}
