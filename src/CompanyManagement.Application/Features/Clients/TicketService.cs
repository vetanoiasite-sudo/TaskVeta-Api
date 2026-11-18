using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Clients;

public record TicketAttachmentDto(int Id, string FileName, string ContentType, long SizeBytes, AttachmentKind Kind, int? MessageId,
    int? DurationSeconds, int UploadedByUserId, string UploadedByName, DateTime CreatedAt);

/// <param name="FromClient">Written by the client (otherwise by the support team).</param>
/// <param name="IsInternal">An internal note: only the support team sees it.</param>
public record TicketMessageDto(int Id, int UserId, string UserName, bool FromClient, string Content, bool IsInternal, DateTime CreatedAt,
    IReadOnlyList<TicketAttachmentDto> Attachments);

/// <param name="Number">Display number, e.g. TK-0042.</param>
/// <param name="AwaitingSupport">The last message is from the client (or there is none yet): the support team should reply.</param>
public record TicketDto(int Id, string Number, int ClientId, string ClientName, string? ClientNameAr, int? ProjectId, string? ProjectName,
    string? ProjectNameAr, string? ProjectCode, string Title, TicketCategory Category, TicketPriority Priority, TicketStatus Status,
    int? AssignedToUserId, string? AssignedToName, string CreatedByName, DateTime CreatedAt, DateTime LastActivityAt, DateTime? ResolvedAt,
    int MessageCount, bool AwaitingSupport);

/// <param name="Attachments">Files sent with the ticket itself (messages carry their own).</param>
/// <param name="CanManage">The current user is on the support team (status, assignee, internal notes).</param>
public record TicketDetailsDto(TicketDto Ticket, string Description, IReadOnlyList<TicketAttachmentDto> Attachments,
    IReadOnlyList<TicketMessageDto> Messages, bool CanManage);

public record CreateTicketRequest(string Title, string Description, TicketCategory Category, TicketPriority Priority, int? ProjectId);

/// <param name="AttachmentIds">Files uploaded to the ticket beforehand, sent with this message.</param>
public record AddTicketMessageRequest(string? Content, bool IsInternal = false, int[]? AttachmentIds = null);

public record UpdateTicketRequest(TicketStatus Status, TicketPriority Priority, TicketCategory Category, int? AssignedToUserId);

/// <summary>People who can be assigned a ticket (the support team).</summary>
public record SupportAgentDto(int UserId, string Name);

public record TicketCountsDto(int Open, int InProgress, int WaitingForClient, int Resolved, int Closed, int AwaitingSupport, int AssignedToMe);

public class TicketQuery : PagedRequest
{
    public TicketStatus? Status { get; set; }
    /// <summary>Only tickets that are not resolved or closed.</summary>
    public bool? OpenOnly { get; set; }
    public TicketCategory? Category { get; set; }
    public TicketPriority? Priority { get; set; }
    public int? ClientId { get; set; }
    public int? ProjectId { get; set; }
    /// <summary>Only tickets assigned to the current user.</summary>
    public bool? Mine { get; set; }
    /// <summary>Only open tickets where the client wrote last (or nobody answered yet).</summary>
    public bool? AwaitingSupport { get; set; }
}

public class CreateTicketRequestValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(8000);
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Priority).IsInEnum();
    }
}

public class AddTicketMessageRequestValidator : AbstractValidator<AddTicketMessageRequest>
{
    public AddTicketMessageRequestValidator()
    {
        RuleFor(x => x.Content).MaximumLength(8000);
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Content) || x.AttachmentIds is { Length: > 0 })
            .WithMessage("Write a message or attach a file.");
        RuleFor(x => x.AttachmentIds).Must(a => a is null || a.Length <= 20).WithMessage("At most 20 files per message.");
    }
}

public class UpdateTicketRequestValidator : AbstractValidator<UpdateTicketRequest>
{
    public UpdateTicketRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.Category).IsInEnum();
    }
}

public interface ITicketService
{
    /// <summary>Clients: their own tickets. Support team: every ticket.</summary>
    Task<PagedResult<TicketDto>> GetPagedAsync(TicketQuery query, CancellationToken ct = default);
    Task<TicketCountsDto> GetCountsAsync(CancellationToken ct = default);
    Task<TicketDetailsDto> GetAsync(int id, CancellationToken ct = default);
    /// <summary>A client opens a ticket (optionally about one of their projects). Files are uploaded to it afterwards.</summary>
    Task<TicketDetailsDto> CreateAsync(CreateTicketRequest request, CancellationToken ct = default);
    Task<TicketDetailsDto> AddMessageAsync(int id, AddTicketMessageRequest request, CancellationToken ct = default);
    /// <summary>Support team: status, priority, category and assignee.</summary>
    Task<TicketDetailsDto> UpdateAsync(int id, UpdateTicketRequest request, CancellationToken ct = default);
    /// <summary>The client closes their own ticket (a later reply reopens it).</summary>
    Task<TicketDetailsDto> CloseByClientAsync(int id, CancellationToken ct = default);
    Task<TicketAttachmentDto> AddAttachmentAsync(int id, string fileName, string? contentType, long size, Stream content, int? durationSeconds,
        CancellationToken ct = default);
    Task<(TicketAttachment Attachment, Stream Content)> OpenAttachmentAsync(int id, int attachmentId, CancellationToken ct = default);
    Task DeleteAttachmentAsync(int id, int attachmentId, CancellationToken ct = default);
    Task<IReadOnlyList<SupportAgentDto>> AgentsAsync(CancellationToken ct = default);
}

/// <summary>
/// Support tickets. Clients see and write on their own tickets only (never internal notes); the support team
/// (support.manage) sees every ticket, assigns it, changes its status and can write internal notes.
/// </summary>
public class TicketService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    ICompanyClock clock,
    IAuditService audit,
    INotificationService notifications,
    IFileStorage storage) : ITicketService
{
    public const int MaxAttachmentsPerTicket = 100;

    private bool IsClient => currentUser.IsInRole(Roles.Client);
    private bool IsSupport => !IsClient && currentUser.HasPermission(Permissions.SupportManage);

    public static string NumberOf(int id) => $"TK-{id:D4}";

    public async Task<PagedResult<TicketDto>> GetPagedAsync(TicketQuery q, CancellationToken ct = default)
    {
        var query = await VisibleAsync(ct);
        if (q.Status is { } status) query = query.Where(t => t.Status == status);
        if (q.OpenOnly == true) query = query.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);
        if (q.Category is { } category) query = query.Where(t => t.Category == category);
        if (q.Priority is { } priority) query = query.Where(t => t.Priority == priority);
        if (q.ClientId is { } clientId) query = query.Where(t => t.ClientId == clientId);
        if (q.ProjectId is { } projectId) query = query.Where(t => t.ProjectId == projectId);
        if (q.Mine == true) query = query.Where(t => t.AssignedToUserId == currentUser.UserId);
        if (q.AwaitingSupport == true)
            query = query.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed
                                     && (!t.Messages.Any(m => !m.IsInternal)
                                         || t.Messages.Where(m => !m.IsInternal).OrderByDescending(m => m.CreatedAt).First().UserId == t.Client.UserId));
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            var number = s.StartsWith("tk-") && int.TryParse(s[3..], out var n) ? n : int.TryParse(s, out var m) ? m : -1;
            query = query.Where(t => t.Id == number || t.Title.ToLower().Contains(s) || t.Client.Name.ToLower().Contains(s));
        }

        var desc = q.SortDir is null || q.Descending;
        query = q.SortBy?.ToLowerInvariant() switch
        {
            "created" => desc ? query.OrderByDescending(t => t.CreatedAt) : query.OrderBy(t => t.CreatedAt),
            "priority" => desc ? query.OrderByDescending(t => t.Priority).ThenByDescending(t => t.LastActivityAt)
                : query.OrderBy(t => t.Priority).ThenByDescending(t => t.LastActivityAt),
            "status" => desc ? query.OrderByDescending(t => t.Status) : query.OrderBy(t => t.Status),
            _ => desc ? query.OrderByDescending(t => t.LastActivityAt) : query.OrderBy(t => t.LastActivityAt),
        };
        var page = await Project(query, IsClient).ToPagedAsync(q, ct);
        return page with { Items = page.Items.Select(t => t with { Number = NumberOf(t.Id) }).ToList() };
    }

    public async Task<TicketCountsDto> GetCountsAsync(CancellationToken ct = default)
    {
        var query = await VisibleAsync(ct);
        var byStatus = await query.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var open = query.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);
        var awaiting = await open.CountAsync(t => !t.Messages.Any(m => !m.IsInternal)
            || t.Messages.Where(m => !m.IsInternal).OrderByDescending(m => m.CreatedAt).First().UserId == t.Client.UserId, ct);
        var mine = await open.CountAsync(t => t.AssignedToUserId == currentUser.UserId, ct);
        int C(TicketStatus s) => byStatus.FirstOrDefault(b => b.Key == s)?.Count ?? 0;
        return new TicketCountsDto(C(TicketStatus.Open), C(TicketStatus.InProgress), C(TicketStatus.WaitingForClient), C(TicketStatus.Resolved),
            C(TicketStatus.Closed), awaiting, mine);
    }

    public async Task<TicketDetailsDto> GetAsync(int id, CancellationToken ct = default)
    {
        var query = await VisibleAsync(ct);
        var ticket = await Project(query.Where(t => t.Id == id), IsClient).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Ticket", id);
        ticket = ticket with { Number = NumberOf(ticket.Id) };
        var description = await db.SupportTickets.AsNoTracking().Where(t => t.Id == id).Select(t => t.Description).FirstAsync(ct);
        var clientUserId = await db.SupportTickets.Where(t => t.Id == id).Select(t => t.Client.UserId).FirstAsync(ct);

        var attachments = await db.TicketAttachments.AsNoTracking().Where(a => a.TicketId == id)
            .Where(a => !IsClient || a.Message == null || !a.Message.IsInternal)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.FileName, a.ContentType, a.SizeBytes, a.MessageId, a.DurationSeconds, a.UploadedByUserId, a.UploadedBy.FullName, a.CreatedAt })
            .ToListAsync(ct);
        var dtos = attachments.Select(a => new TicketAttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes,
            AttachmentRules.KindOfStored(a.FileName, a.ContentType), a.MessageId, a.DurationSeconds, a.UploadedByUserId, a.FullName, a.CreatedAt)).ToList();

        var messages = await db.TicketMessages.AsNoTracking().Where(m => m.TicketId == id && (!IsClient || !m.IsInternal))
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.UserId, m.User.FullName, m.Content, m.IsInternal, m.CreatedAt })
            .ToListAsync(ct);
        var messageDtos = messages.Select(m => new TicketMessageDto(m.Id, m.UserId, m.FullName, m.UserId == clientUserId, m.Content, m.IsInternal,
            m.CreatedAt, dtos.Where(a => a.MessageId == m.Id).ToList())).ToList();

        // Ticket-level files: sent with the ticket (no message). Unsent uploads of other people are not shown.
        var ticketFiles = dtos.Where(a => a.MessageId is null && a.UploadedByUserId == clientUserId).ToList();
        return new TicketDetailsDto(ticket, description, ticketFiles, messageDtos, IsSupport);
    }

    public async Task<TicketDetailsDto> CreateAsync(CreateTicketRequest r, CancellationToken ct = default)
    {
        var client = await CurrentClientAsync(ct);
        if (r.ProjectId is int projectId && !await db.ClientProjects.AnyAsync(cp => cp.ClientId == client.Id && cp.ProjectId == projectId, ct))
            throw new BadRequestException("Choose one of your projects.");
        var now = clock.UtcNow;
        var ticket = new SupportTicket
        {
            ClientId = client.Id, ProjectId = r.ProjectId, Title = r.Title.Trim(), Description = r.Description.Trim(), Category = r.Category,
            Priority = r.Priority, Status = TicketStatus.Open, CreatedByUserId = currentUser.UserId!.Value, LastActivityAt = now,
        };
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        audit.Log("Create", nameof(SupportTicket), ticket.Id, newValues: new { ticket.Title, Category = r.Category.ToString(), r.ProjectId });

        var number = NumberOf(ticket.Id);
        foreach (var uid in await SupportUserIdsAsync(ct))
            notifications.Add(uid, NotificationType.TicketCreated, $"New ticket {number}", $"{client.Name}: {ticket.Title}",
                $"تذكرة جديدة {number}", $"{client.NameAr ?? client.Name}: {ticket.Title}", $"/support/{ticket.Id}", "Ticket", ticket.Id);
        await notifications.SaveAndDispatchAsync(ct);
        return await GetAsync(ticket.Id, ct);
    }

    public async Task<TicketDetailsDto> AddMessageAsync(int id, AddTicketMessageRequest r, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(id, ct);
        var isInternal = r.IsInternal && IsSupport;
        var ids = (r.AttachmentIds ?? []).Distinct().ToArray();
        var files = ids.Length == 0 ? [] : await db.TicketAttachments
            .Where(a => ids.Contains(a.Id) && a.TicketId == id && a.MessageId == null && a.UploadedByUserId == currentUser.UserId).ToListAsync(ct);
        if (files.Count != ids.Length) throw new BadRequestException("Some files were not found. Upload them again.");

        var now = clock.UtcNow;
        var message = new TicketMessage { TicketId = id, UserId = currentUser.UserId!.Value, Content = r.Content?.Trim() ?? string.Empty, IsInternal = isInternal };
        db.TicketMessages.Add(message);
        foreach (var f in files) f.Message = message;

        var number = NumberOf(id);
        if (!isInternal)
        {
            ticket.LastActivityAt = now;
            if (IsClient)
            {
                // A client reply re-opens a resolved ticket and answers a "waiting for you".
                if (ticket.Status is TicketStatus.Resolved or TicketStatus.Closed or TicketStatus.WaitingForClient)
                {
                    ticket.Status = ticket.AssignedToUserId is null ? TicketStatus.Open : TicketStatus.InProgress;
                    ticket.ResolvedAt = null;
                    ticket.ClosedAt = null;
                }
                var recipients = ticket.AssignedToUserId is int assignee ? [assignee] : await SupportUserIdsAsync(ct);
                foreach (var uid in recipients)
                    notifications.Add(uid, NotificationType.TicketReply, $"Client reply on {number}", $"{ticket.Client.Name}: {ticket.Title}",
                        $"رد من العميل على {number}", $"{ticket.Client.NameAr ?? ticket.Client.Name}: {ticket.Title}", $"/support/{id}", "Ticket", id);
            }
            else
            {
                if (ticket.Status == TicketStatus.Open) ticket.Status = TicketStatus.InProgress;
                notifications.Add(ticket.Client.UserId, NotificationType.TicketReply, $"New reply on {number}", ticket.Title,
                    $"رد جديد على {number}", ticket.Title, $"/portal/tickets/{id}", "Ticket", id);
            }
        }
        await db.SaveChangesAsync(ct);
        await notifications.SaveAndDispatchAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<TicketDetailsDto> UpdateAsync(int id, UpdateTicketRequest r, CancellationToken ct = default)
    {
        if (!IsSupport) throw new ForbiddenException();
        var ticket = await LoadAsync(id, ct);
        if (r.AssignedToUserId is int assignee && !(await AgentsAsync(ct)).Any(a => a.UserId == assignee))
            throw new BadRequestException("The ticket can only be assigned to someone on the support team.");

        var old = new { Status = ticket.Status.ToString(), Priority = ticket.Priority.ToString(), Category = ticket.Category.ToString(), ticket.AssignedToUserId };
        var statusChanged = ticket.Status != r.Status;
        var now = clock.UtcNow;
        if (statusChanged)
        {
            ticket.ResolvedAt = r.Status is TicketStatus.Resolved or TicketStatus.Closed ? ticket.ResolvedAt ?? now : null;
            ticket.ClosedAt = r.Status == TicketStatus.Closed ? now : null;
            ticket.LastActivityAt = now;
        }
        var newAssignee = r.AssignedToUserId != ticket.AssignedToUserId ? r.AssignedToUserId : null;
        ticket.Status = r.Status;
        ticket.Priority = r.Priority;
        ticket.Category = r.Category;
        ticket.AssignedToUserId = r.AssignedToUserId;
        audit.Log("Update", nameof(SupportTicket), id, old,
            new { Status = r.Status.ToString(), Priority = r.Priority.ToString(), Category = r.Category.ToString(), r.AssignedToUserId });

        var number = NumberOf(id);
        if (statusChanged)
            notifications.Add(ticket.Client.UserId, NotificationType.TicketStatusChanged, $"{number}: {r.Status}", ticket.Title,
                $"{number}: {StatusAr(r.Status)}", ticket.Title, $"/portal/tickets/{id}", "Ticket", id);
        if (newAssignee is int a && a != currentUser.UserId)
            notifications.Add(a, NotificationType.TicketStatusChanged, $"Ticket {number} assigned to you", $"{ticket.Client.Name}: {ticket.Title}",
                $"تم إسناد التذكرة {number} إليك", $"{ticket.Client.NameAr ?? ticket.Client.Name}: {ticket.Title}", $"/support/{id}", "Ticket", id);
        await db.SaveChangesAsync(ct);
        await notifications.SaveAndDispatchAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<TicketDetailsDto> CloseByClientAsync(int id, CancellationToken ct = default)
    {
        if (!IsClient) throw new ForbiddenException();
        var ticket = await LoadAsync(id, ct);
        if (ticket.Status != TicketStatus.Closed)
        {
            var now = clock.UtcNow;
            ticket.Status = TicketStatus.Closed;
            ticket.ClosedAt = now;
            ticket.ResolvedAt ??= now;
            ticket.LastActivityAt = now;
            audit.Log("CloseByClient", nameof(SupportTicket), id);
            if (ticket.AssignedToUserId is int assignee)
                notifications.Add(assignee, NotificationType.TicketStatusChanged, $"{NumberOf(id)} closed by the client", ticket.Title,
                    $"أغلق العميل التذكرة {NumberOf(id)}", ticket.Title, $"/support/{id}", "Ticket", id);
            await db.SaveChangesAsync(ct);
            await notifications.SaveAndDispatchAsync(ct);
        }
        return await GetAsync(id, ct);
    }

    public async Task<TicketAttachmentDto> AddAttachmentAsync(int id, string fileName, string? contentType, long size, Stream content,
        int? durationSeconds, CancellationToken ct = default)
    {
        await LoadAsync(id, ct);
        if (await db.TicketAttachments.CountAsync(a => a.TicketId == id, ct) >= MaxAttachmentsPerTicket)
            throw new BadRequestException($"A ticket can have at most {MaxAttachmentsPerTicket} files.");
        var file = await UploadedFile.CheckAsync(fileName, contentType, size, content, ct);
        var stored = await storage.SaveAsync(file.Content, file.Extension, ct);
        var att = new TicketAttachment
        {
            TicketId = id, UploadedByUserId = currentUser.UserId!.Value, FileName = file.SafeName, StoredFileName = stored,
            ContentType = file.ContentType, SizeBytes = size, DurationSeconds = durationSeconds is > 0 and < 24 * 3600 ? durationSeconds : null,
        };
        db.TicketAttachments.Add(att);
        try { await db.SaveChangesAsync(ct); }
        catch { storage.Delete(stored); throw; }
        var name = await db.Users.Where(u => u.Id == att.UploadedByUserId).Select(u => u.FullName).FirstAsync(ct);
        return new TicketAttachmentDto(att.Id, att.FileName, att.ContentType, att.SizeBytes, file.Kind, null, att.DurationSeconds,
            att.UploadedByUserId, name, att.CreatedAt);
    }

    public async Task<(TicketAttachment Attachment, Stream Content)> OpenAttachmentAsync(int id, int attachmentId, CancellationToken ct = default)
    {
        await LoadAsync(id, ct);
        var att = await db.TicketAttachments.AsNoTracking().Include(a => a.Message)
                      .FirstOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == id, ct)
                  ?? throw new NotFoundException("Attachment", attachmentId);
        if (IsClient && att.Message?.IsInternal == true) throw new NotFoundException("Attachment", attachmentId);
        return (att, storage.OpenRead(att.StoredFileName));
    }

    public async Task DeleteAttachmentAsync(int id, int attachmentId, CancellationToken ct = default)
    {
        await LoadAsync(id, ct);
        var att = await db.TicketAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == id, ct)
                  ?? throw new NotFoundException("Attachment", attachmentId);
        // The uploader can remove a file not sent yet; the support team can remove any file.
        if (!IsSupport && (att.UploadedByUserId != currentUser.UserId || att.MessageId is not null)) throw new ForbiddenException();
        db.TicketAttachments.Remove(att);
        await db.SaveChangesAsync(ct);
        storage.Delete(att.StoredFileName);
    }

    public async Task<IReadOnlyList<SupportAgentDto>> AgentsAsync(CancellationToken ct = default)
    {
        if (!IsSupport) throw new ForbiddenException();
        var ids = await SupportUserIdsAsync(ct);
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).OrderBy(u => u.FullName)
            .Select(u => new SupportAgentDto(u.Id, u.FullName)).ToListAsync(ct);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Tickets the current user may see: a client's own, or all of them for the support team.</summary>
    private async Task<IQueryable<SupportTicket>> VisibleAsync(CancellationToken ct)
    {
        var query = db.SupportTickets.AsNoTracking();
        if (IsClient)
        {
            var client = await CurrentClientAsync(ct);
            return query.Where(t => t.ClientId == client.Id);
        }
        if (!IsSupport) throw new ForbiddenException("You cannot see support tickets.");
        return query;
    }

    private async Task<SupportTicket> LoadAsync(int id, CancellationToken ct)
    {
        var ticket = await db.SupportTickets.Include(t => t.Client).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Ticket", id);
        if (IsClient)
        {
            if (ticket.Client.UserId != currentUser.UserId) throw new NotFoundException("Ticket", id);
        }
        else if (!IsSupport) throw new ForbiddenException("You cannot see support tickets.");
        return ticket;
    }

    private async Task<Client> CurrentClientAsync(CancellationToken ct) =>
        await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == currentUser.UserId, ct)
        ?? throw new ForbiddenException("This account is not linked to a client.");

    /// <summary>Active users whose roles include support.manage (super admins, admins, managers).</summary>
    private async Task<int[]> SupportUserIdsAsync(CancellationToken ct)
    {
        var roles = Permissions.ByRole.Where(kv => kv.Value.Contains(Permissions.SupportManage) && kv.Key != Roles.Leader).Select(kv => kv.Key).ToArray();
        return await db.UserRoles.Where(ur => db.Roles.Any(r => r.Id == ur.RoleId && roles.Contains(r.Name!)))
            .Select(ur => ur.UserId).Distinct()
            .Where(uid => db.Users.Any(u => u.Id == uid && u.IsActive)).ToArrayAsync(ct);
    }

    private static IQueryable<TicketDto> Project(IQueryable<SupportTicket> query, bool forClient) => query.Select(t => new TicketDto(
        t.Id, "", t.ClientId, t.Client.Name, t.Client.NameAr, t.ProjectId,
        t.Project != null ? t.Project.Name : null, t.Project != null ? t.Project.NameAr : null, t.Project != null ? t.Project.Code : null,
        t.Title, t.Category, t.Priority, t.Status, t.AssignedToUserId, t.AssignedTo != null ? t.AssignedTo.FullName : null, t.CreatedBy.FullName,
        t.CreatedAt, t.LastActivityAt, t.ResolvedAt,
        t.Messages.Count(m => !forClient || !m.IsInternal),
        t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed
        && (!t.Messages.Any(m => !m.IsInternal)
            || t.Messages.Where(m => !m.IsInternal).OrderByDescending(m => m.CreatedAt).First().UserId == t.Client.UserId)));

    private static string StatusAr(TicketStatus s) => s switch
    {
        TicketStatus.Open => "مفتوحة",
        TicketStatus.InProgress => "قيد المعالجة",
        TicketStatus.WaitingForClient => "بانتظار ردك",
        TicketStatus.Resolved => "تم الحل",
        TicketStatus.Closed => "مغلقة",
        _ => s.ToString(),
    };
}
