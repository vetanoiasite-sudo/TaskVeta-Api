using System.Linq.Expressions;
using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Tasks;

public interface ITaskService
{
    Task<PagedResult<TaskDto>> GetPagedAsync(TaskQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<TaskDto>> GetAllForExportAsync(TaskQuery query, CancellationToken ct = default);
    Task<TaskDetailsDto> GetAsync(int id, CancellationToken ct = default);
    Task<TaskDetailsDto> CreateAsync(CreateTaskRequest request, CancellationToken ct = default);
    Task<TaskDetailsDto> UpdateAsync(int id, UpdateTaskRequest request, CancellationToken ct = default);
    Task<TaskDetailsDto> ChangeStatusAsync(int id, UpdateTaskStatusRequest request, CancellationToken ct = default);
    Task<TaskDetailsDto> UpdateProgressAsync(int id, UpdateTaskProgressRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<TaskCommentDto> AddCommentAsync(int id, AddCommentRequest request, CancellationToken ct = default);
    /// <summary>Records that the current user opened the task (read receipt).</summary>
    Task MarkSeenAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<TaskHistoryDto>> GetHistoryAsync(int id, CancellationToken ct = default);
    Task<TaskAttachmentDto> AddAttachmentAsync(int id, string fileName, string contentType, long size, Stream content, CancellationToken ct = default);
    Task<(TaskAttachment Attachment, Stream Content)> OpenAttachmentAsync(int id, int attachmentId, CancellationToken ct = default);
    Task DeleteAttachmentAsync(int id, int attachmentId, CancellationToken ct = default);
    Task<TaskDashboardDto> GetDashboardAsync(bool mine, CancellationToken ct = default);
    Task<TaskTargetsDto> GetTargetsAsync(CancellationToken ct = default);
    /// <summary>Task figures per project over all of its tasks.</summary>
    Task<IReadOnlyDictionary<int, ProjectTaskStats>> GetProjectStatsAsync(int? projectId, CancellationToken ct = default);
    /// <summary>Projects the current user may open: null = all (task admins / project managers); otherwise projects with at least one task they can see.</summary>
    Task<int[]?> AccessibleProjectIdsAsync(CancellationToken ct = default);
    /// <summary>All tasks of a project (for its timeline) plus the ids of those the current user may open.</summary>
    Task<(IReadOnlyList<TaskDto> Tasks, int[] OpenableIds)> GetProjectTasksAsync(int projectId, CancellationToken ct = default);
}

public class TaskService(
    IApplicationDbContext db,
    IAccessScope scope,
    ICurrentUser currentUser,
    ICompanyClock clock,
    IAuditService audit,
    INotificationService notifications,
    IFileStorage storage) : ITaskService
{
    public static readonly string[] AllowedExtensions =
        [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".png", ".jpg", ".jpeg", ".gif", ".zip"];
    public const long MaxAttachmentBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, Expression<Func<TaskItem, object?>>> Sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = t => t.Title,
        ["priority"] = t => t.Priority,
        ["status"] = t => t.Status,
        ["dueDate"] = t => t.DueDate,
        ["startDate"] = t => t.StartDate,
        ["progressPercent"] = t => t.ProgressPercent,
        ["createdAt"] = t => t.CreatedAt,
        ["updatedAt"] = t => t.UpdatedAt,
    };

    private Expression<Func<TaskItem, TaskDto>> Projection(DateTime now) => t => new TaskDto(
        t.Id, t.Title, t.Priority, t.Status, t.ProgressPercent, t.StartDate, t.DueDate, t.CompletionDate,
        t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate != null && t.DueDate < now,
        t.AssignedByUserId, t.AssignedBy.FullName, t.DepartmentId, t.Department != null ? t.Department.Name : null,
        t.TeamId, t.Team != null ? t.Team.Name : null, t.ProjectId, t.Project != null ? t.Project.Name : null,
        t.Assignments.Select(a => new TaskAssigneeDto(a.EmployeeId, a.Employee.EmployeeCode, a.Employee.FirstName + " " + a.Employee.LastName)).ToList(),
        t.Comments.Count, t.Attachments.Count, t.CreatedAt, t.UpdatedAt, t.Rating);

    // ------------------------------------------------------------------ visibility

    private async Task<IQueryable<TaskItem>> VisibleTasksAsync(CancellationToken ct)
    {
        if (currentUser.HasPermission(Permissions.TasksViewAll)) return db.Tasks.AsQueryable();
        var userId = currentUser.UserId ?? -1;
        var me = await scope.GetCurrentEmployeeIdAsync(ct) ?? -1;
        if (currentUser.HasPermission(Permissions.TasksAssign))
        {
            var scoped = (await scope.TaskScopedEmployeesAsync(ct)).Select(e => e.Id);
            return db.Tasks.Where(t => t.AssignedByUserId == userId || t.Assignments.Any(a => scoped.Contains(a.EmployeeId)));
        }
        return db.Tasks.Where(t => t.AssignedByUserId == userId || t.Assignments.Any(a => a.EmployeeId == me));
    }

    private async Task<TaskItem> LoadVisibleAsync(int id, CancellationToken ct, bool includeAssignments = true)
    {
        var visible = await VisibleTasksAsync(ct);
        var q = includeAssignments ? visible.Include(t => t.Assignments) : visible;
        return await q.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Task", id);
    }

    /// <summary>Reviewer = full task admin, the creator, or a manager whose scope covers every assignee.</summary>
    private async Task<bool> IsReviewerAsync(TaskItem task, CancellationToken ct)
    {
        if (currentUser.HasPermission(Permissions.TasksViewAll) && currentUser.HasPermission(Permissions.TasksReview)) return true;
        if (!currentUser.HasPermission(Permissions.TasksReview)) return false;
        if (task.AssignedByUserId == currentUser.UserId) return true;
        var me = await scope.GetCurrentEmployeeIdAsync(ct);
        var others = task.Assignments.Select(a => a.EmployeeId).Where(id => id != me).ToList();
        if (others.Count == 0) return false;
        var scoped = await scope.TaskScopedEmployeesAsync(ct);
        var inScope = await scoped.CountAsync(e => others.Contains(e.Id), ct);
        return inScope == others.Count;
    }

    private async Task<bool> IsAssigneeAsync(TaskItem task, CancellationToken ct)
    {
        var me = await scope.GetCurrentEmployeeIdAsync(ct);
        return me is int id && task.Assignments.Any(a => a.EmployeeId == id);
    }

    // ------------------------------------------------------------------ queries

    public async Task<PagedResult<TaskDto>> GetPagedAsync(TaskQuery q, CancellationToken ct = default)
    {
        var query = await FilterAsync(q, ct);
        return await query.ApplySort(q, Sorts, "createdAt", defaultDesc: true).Select(Projection(clock.UtcNow)).ToPagedAsync(q, ct);
    }

    public async Task<IReadOnlyList<TaskDto>> GetAllForExportAsync(TaskQuery q, CancellationToken ct = default)
    {
        var query = await FilterAsync(q, ct);
        return await query.ApplySort(q, Sorts, "createdAt", defaultDesc: true).Select(Projection(clock.UtcNow)).Take(20_000).ToListAsync(ct);
    }

    private async Task<IQueryable<TaskItem>> FilterAsync(TaskQuery q, CancellationToken ct)
    {
        var query = (await VisibleTasksAsync(ct)).AsNoTracking();
        var now = clock.UtcNow;
        if (q.Mine)
        {
            var me = await scope.GetCurrentEmployeeIdAsync(ct) ?? -1;
            query = query.Where(t => t.Assignments.Any(a => a.EmployeeId == me));
        }
        if (q.CreatedByMe) query = query.Where(t => t.AssignedByUserId == currentUser.UserId);
        if (q.Status is TaskItemStatus s) query = query.Where(t => t.Status == s);
        if (q.Priority is TaskPriority p) query = query.Where(t => t.Priority == p);
        if (q.AssigneeId is int a) query = query.Where(t => t.Assignments.Any(x => x.EmployeeId == a));
        if (q.DepartmentId is int d) query = query.Where(t => t.DepartmentId == d);
        if (q.TeamId is int tm) query = query.Where(t => t.TeamId == tm);
        if (q.ProjectId is int pr) query = query.Where(t => t.ProjectId == pr);
        if (q.Overdue == true)
            query = query.Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate != null && t.DueDate < now);
        if (q.DueFrom is DateTime df) query = query.Where(t => t.DueDate >= df);
        if (q.DueTo is DateTime dt) query = query.Where(t => t.DueDate <= dt);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim().ToLower();
            query = query.Where(t => t.Title.ToLower().Contains(term) || (t.Description != null && t.Description.ToLower().Contains(term)));
        }
        return query;
    }

    public async Task<TaskDetailsDto> GetAsync(int id, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        var now = clock.UtcNow;
        var dto = await db.Tasks.AsNoTracking().Where(t => t.Id == id).Select(Projection(now)).FirstAsync(ct);
        var comments = await db.TaskComments.AsNoTracking().Where(c => c.TaskId == id).OrderBy(c => c.CreatedAt)
            .Select(c => new TaskCommentDto(c.Id, c.UserId, c.User.FullName, c.Content, c.CreatedAt, c.MentionedUserIds)).ToListAsync(ct);
        var attachments = await db.TaskAttachments.AsNoTracking().Where(a => a.TaskId == id).OrderBy(a => a.CreatedAt)
            .Select(a => new TaskAttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedByUserId, a.UploadedBy.FullName, a.CreatedAt))
            .ToListAsync(ct);

        var reviewer = await IsReviewerAsync(task, ct);
        var assignee = await IsAssigneeAsync(task, ct);
        var allowed = Enum.GetValues<TaskItemStatus>().Where(s => TaskWorkflow.CanTransition(task.Status, s, reviewer, assignee)).ToArray();
        var perms = new TaskPermissionsDto(reviewer, reviewer, (assignee || reviewer) && !task.IsClosed, true, allowed);
        var participants = await ParticipantsAsync(task, ct);
        var reviews = await db.TaskReviews.AsNoTracking().Where(r => r.TaskId == id).OrderByDescending(r => r.CreatedAt)
            .Select(r => new TaskReviewDto(r.Id, r.Approved, r.Rating, r.Notes, r.ReviewerUserId, r.Reviewer.FullName, r.CreatedAt)).ToListAsync(ct);
        return new TaskDetailsDto(dto, task.Description, task.ReviewNotes, comments, attachments, perms, task.RowVersion.ToString(), participants, reviews);
    }

    // ------------------------------------------------------------------ commands

    public async Task<TaskDetailsDto> CreateAsync(CreateTaskRequest r, CancellationToken ct = default)
    {
        if (!currentUser.HasPermission(Permissions.TasksAssign)) throw new ForbiddenException("You cannot assign tasks.");
        var assignees = await ValidateAssigneesAsync(r.AssigneeEmployeeIds, ct);
        var (departmentId, teamId) = await ResolveTargetAsync(r.DepartmentId, r.TeamId, assignees, ct);
        var projectId = await ValidateProjectAsync(r.ProjectId, null, ct);

        var task = new TaskItem
        {
            Title = r.Title.Trim(), Description = r.Description, Priority = r.Priority, StartDate = r.StartDate, DueDate = r.DueDate,
            DepartmentId = departmentId, TeamId = teamId, ProjectId = projectId, AssignedByUserId = currentUser.UserId!.Value,
            AssignedToEmployeeId = assignees.FirstOrDefault()?.Id,
            Status = assignees.Count > 0 ? TaskItemStatus.Assigned : TaskItemStatus.New
        };
        foreach (var e in assignees)
            task.Assignments.Add(new TaskAssignment { EmployeeId = e.Id, AssignedByUserId = task.AssignedByUserId });
        task.History.Add(new TaskHistory
        {
            ChangedByUserId = currentUser.UserId, Action = "Created", ToStatus = task.Status,
            Details = assignees.Count > 0 ? $"Assigned to {string.Join(", ", assignees.Select(a => a.Name))}" : null
        });
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);

        audit.Log("Create", "Task", task.Id, newValues: new { r.Title, r.Priority, r.DueDate, departmentId, teamId, projectId, Assignees = r.AssigneeEmployeeIds });
        NotifyAssigned(task, assignees.Select(a => a.Id));
        await notifications.SaveAndDispatchAsync(ct);
        return await GetAsync(task.Id, ct);
    }

    public async Task<TaskDetailsDto> UpdateAsync(int id, UpdateTaskRequest r, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        if (!await IsReviewerAsync(task, ct)) throw new ForbiddenException("You cannot edit this task.");
        if (!string.IsNullOrEmpty(r.RowVersion) && task.RowVersion.ToString() != r.RowVersion)
            throw new ConflictException("The task was modified by someone else. Reload and try again.");

        var assignees = await ValidateAssigneesAsync(r.AssigneeEmployeeIds, ct);
        var old = new { task.Title, task.Priority, task.DueDate, task.StartDate, task.DepartmentId, task.TeamId, task.ProjectId, Assignees = task.Assignments.Select(a => a.EmployeeId).ToArray() };

        task.Title = r.Title.Trim();
        task.Description = r.Description;
        task.Priority = r.Priority;
        task.StartDate = r.StartDate;
        if (task.DueDate != r.DueDate) { task.DeadlineReminderSentAt = null; task.OverdueNotifiedAt = null; }
        task.DueDate = r.DueDate;
        (task.DepartmentId, task.TeamId) = await ResolveTargetAsync(r.DepartmentId, r.TeamId, assignees, ct);
        task.ProjectId = await ValidateProjectAsync(r.ProjectId, old.ProjectId, ct);

        var desired = assignees.Select(a => a.Id).ToHashSet();
        var removed = task.Assignments.Where(a => !desired.Contains(a.EmployeeId)).ToList();
        var added = desired.Except(task.Assignments.Select(a => a.EmployeeId)).ToList();
        db.TaskAssignments.RemoveRange(removed);
        foreach (var empId in added)
            db.TaskAssignments.Add(new TaskAssignment { TaskId = id, EmployeeId = empId, AssignedByUserId = currentUser.UserId!.Value });
        task.AssignedToEmployeeId = assignees.FirstOrDefault()?.Id;
        if (task.Status == TaskItemStatus.New && desired.Count > 0) task.Status = TaskItemStatus.Assigned;

        var details = new List<string>();
        if (old.Title != task.Title) details.Add("title");
        if (old.Priority != task.Priority) details.Add($"priority → {task.Priority}");
        if (old.DueDate != task.DueDate) details.Add($"due date → {task.DueDate:yyyy-MM-dd HH:mm}");
        if (old.ProjectId != task.ProjectId) details.Add("project");
        if (added.Count > 0) details.Add($"added {added.Count} assignee(s)");
        if (removed.Count > 0) details.Add($"removed {removed.Count} assignee(s)");
        db.TaskHistory.Add(new TaskHistory
        {
            TaskId = id, ChangedByUserId = currentUser.UserId, Action = "Updated",
            Details = details.Count > 0 ? "Changed " + string.Join(", ", details) : "Details updated"
        });
        audit.Log("Update", "Task", id, old, new { r.Title, r.Priority, r.DueDate, r.StartDate, task.DepartmentId, task.TeamId, task.ProjectId, Assignees = r.AssigneeEmployeeIds });
        NotifyAssigned(task, added);

        try { await notifications.SaveAndDispatchAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The task was modified by someone else. Reload and try again."); }
        return await GetAsync(id, ct);
    }

    public async Task<TaskDetailsDto> ChangeStatusAsync(int id, UpdateTaskStatusRequest r, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        var reviewer = await IsReviewerAsync(task, ct);
        var assignee = await IsAssigneeAsync(task, ct);
        if (!TaskWorkflow.CanTransition(task.Status, r.Status, reviewer, assignee))
            throw new BadRequestException($"You cannot change the status from {task.Status} to {r.Status}.");

        var from = task.Status;
        task.Status = r.Status;
        var now = clock.UtcNow;
        switch (r.Status)
        {
            case TaskItemStatus.InProgress when task.StartDate is null:
                task.StartDate = now;
                break;
            case TaskItemStatus.PendingReview:
                task.ProgressPercent = 100;
                break;
            case TaskItemStatus.Completed:
                task.CompletionDate = now;
                task.ProgressPercent = 100;
                task.ReviewNotes = r.Note;
                task.Rating = r.Rating;
                break;
            case TaskItemStatus.Rejected:
                task.ReviewNotes = r.Note;
                task.CompletionDate = null;
                task.Rating = null;
                break;
            default:
                if (from == TaskItemStatus.Completed) task.Rating = null; // reopened: the old rating no longer applies
                break;
        }
        // Approving / rejecting a submitted task is a review: keep it (with rating and notes) for the review report.
        if (r.Status is TaskItemStatus.Completed or TaskItemStatus.Rejected)
            db.TaskReviews.Add(new TaskReview
            {
                TaskId = id, ReviewerUserId = currentUser.UserId!.Value, Approved = r.Status == TaskItemStatus.Completed,
                Rating = r.Rating, Notes = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim(),
            });
        if (r.Status != TaskItemStatus.Completed) task.CompletionDate = null;
        if (r.ProgressPercent is int p && r.Status is not (TaskItemStatus.Completed or TaskItemStatus.PendingReview)) task.ProgressPercent = p;

        var historyNote = r.Rating is int rating ? $"Rating {rating}/10" + (string.IsNullOrWhiteSpace(r.Note) ? "" : $" · {r.Note}") : r.Note;
        db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "StatusChanged", FromStatus = from, ToStatus = r.Status, Details = historyNote });
        audit.Log("ChangeStatus", "Task", id, new { Status = from.ToString() }, new { Status = r.Status.ToString() }, r.Note);

        await NotifyStatusChangeAsync(task, from, r, ct);
        try { await notifications.SaveAndDispatchAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The task was modified by someone else. Reload and try again."); }
        return await GetAsync(id, ct);
    }

    public async Task<TaskDetailsDto> UpdateProgressAsync(int id, UpdateTaskProgressRequest r, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        if (!await IsAssigneeAsync(task, ct) && !await IsReviewerAsync(task, ct)) throw new ForbiddenException("You cannot update this task.");
        if (task.IsClosed) throw new ConflictException("Closed tasks cannot be updated.");
        var from = task.ProgressPercent;
        task.ProgressPercent = r.ProgressPercent;
        if (task.Status is TaskItemStatus.New or TaskItemStatus.Assigned && r.ProgressPercent > 0 && await IsAssigneeAsync(task, ct))
        {
            db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "StatusChanged", FromStatus = task.Status, ToStatus = TaskItemStatus.InProgress, Details = "Work started" });
            task.Status = TaskItemStatus.InProgress;
            task.StartDate ??= clock.UtcNow;
        }
        db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "ProgressUpdated", Details = $"{from}% → {r.ProgressPercent}%" + (string.IsNullOrWhiteSpace(r.Note) ? "" : $": {r.Note}") });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        if (!await IsReviewerAsync(task, ct)) throw new ForbiddenException("You cannot delete this task.");
        var files = await db.TaskAttachments.Where(a => a.TaskId == id).Select(a => a.StoredFileName).ToListAsync(ct);
        db.Tasks.Remove(task); // cascades to assignments/comments/attachments/history
        audit.Log("Delete", "Task", id, new { task.Title, Status = task.Status.ToString() });
        await db.SaveChangesAsync(ct);
        foreach (var f in files) storage.Delete(f);
    }

    public async Task<TaskCommentDto> AddCommentAsync(int id, AddCommentRequest r, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        var me = currentUser.UserId!.Value;
        // Only people involved in the task can be mentioned (they are the ones who can open it).
        var participantIds = (await ParticipantsAsync(task, ct)).Select(p => p.UserId).ToHashSet();
        var mentioned = (r.MentionedUserIds ?? []).Distinct().Where(u => u != me).ToArray();
        if (mentioned.Any(u => !participantIds.Contains(u)))
            throw new BadRequestException("You can only mention people involved in this task.");

        var comment = new TaskComment { TaskId = id, UserId = me, Content = r.Content.Trim(), MentionedUserIds = mentioned };
        db.TaskComments.Add(comment);
        db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "Commented" });
        await TouchViewAsync(id, me, ct); // the author has obviously seen the task

        var name = await db.Users.Where(u => u.Id == me).Select(u => u.FullName).FirstAsync(ct);
        foreach (var uid in mentioned)
            notifications.Add(uid, NotificationType.TaskMention, "You were mentioned",
                $"{name} mentioned you in \"{task.Title}\": {Excerpt(comment.Content)}", "تمت الإشارة إليك",
                $"أشار إليك {name} في المهمة \"{task.Title}\": {Excerpt(comment.Content)}", $"/tasks/{id}", "Task", id);
        var recipients = await RelatedUserIdsAsync(task, ct);
        foreach (var uid in recipients.Where(u => u != me && !mentioned.Contains(u)))
            notifications.Add(uid, NotificationType.TaskComment, "New comment",
                $"{name} commented on \"{task.Title}\".", "تعليق جديد", $"علّق {name} على \"{task.Title}\".",
                $"/tasks/{id}", "Task", id);
        await notifications.SaveAndDispatchAsync(ct);
        return new TaskCommentDto(comment.Id, comment.UserId, name, comment.Content, comment.CreatedAt, comment.MentionedUserIds);
    }

    private static string Excerpt(string text) => text.Length <= 120 ? text : text[..117] + "...";

    public async Task MarkSeenAsync(int id, CancellationToken ct = default)
    {
        await LoadVisibleAsync(id, ct, includeAssignments: false);
        await TouchViewAsync(id, currentUser.UserId!.Value, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* same user opened the task twice at once: the other request recorded the view */ }
    }

    private async Task TouchViewAsync(int taskId, int userId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var view = await db.TaskViews.FirstOrDefaultAsync(v => v.TaskId == taskId && v.UserId == userId, ct);
        if (view is null) db.TaskViews.Add(new TaskView { TaskId = taskId, UserId = userId, FirstSeenAt = now, LastSeenAt = now });
        else view.LastSeenAt = now;
    }

    /// <summary>
    /// People involved in the task, with read receipts: assignees (with accounts), the creator, and the head of the task's
    /// department / leader of its team. A person is listed once, with their most specific role.
    /// </summary>
    private async Task<List<TaskParticipantDto>> ParticipantsAsync(TaskItem task, CancellationToken ct)
    {
        var assigneeIds = task.Assignments.Select(a => a.EmployeeId).ToList();
        var leaderEmployeeIds = new List<int>();
        if (task.DepartmentId is int d && await db.Departments.Where(x => x.Id == d).Select(x => x.ManagerEmployeeId).FirstOrDefaultAsync(ct) is int head)
            leaderEmployeeIds.Add(head);
        if (task.TeamId is int t && await db.Teams.Where(x => x.Id == t).Select(x => x.LeaderEmployeeId).FirstOrDefaultAsync(ct) is int lead)
            leaderEmployeeIds.Add(lead);

        var employeeIds = assigneeIds.Concat(leaderEmployeeIds).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id) && e.UserId != null && !e.IsDeleted)
            .Select(e => new { e.Id, UserId = e.UserId!.Value, Name = e.FirstName + " " + e.LastName }).ToListAsync(ct);

        var list = new List<(int UserId, string Name, int? EmployeeId, string Role)>();
        void Add(int userId, string name, int? employeeId, string role)
        {
            if (list.All(p => p.UserId != userId)) list.Add((userId, name, employeeId, role));
        }
        foreach (var id in assigneeIds)
            if (employees.FirstOrDefault(e => e.Id == id) is { } e) Add(e.UserId, e.Name, e.Id, "Assignee");
        var creator = await db.Users.AsNoTracking().Where(u => u.Id == task.AssignedByUserId).Select(u => new { u.Id, u.FullName }).FirstAsync(ct);
        var creatorEmployee = await db.Employees.Where(e => e.UserId == creator.Id).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        Add(creator.Id, creator.FullName, creatorEmployee, "Creator");
        foreach (var id in leaderEmployeeIds)
            if (employees.FirstOrDefault(e => e.Id == id) is { } e) Add(e.UserId, e.Name, e.Id, "Leader");

        var userIds = list.Select(p => p.UserId).ToList();
        var views = await db.TaskViews.AsNoTracking().Where(v => v.TaskId == task.Id && userIds.Contains(v.UserId))
            .ToDictionaryAsync(v => v.UserId, ct);
        return list.Select(p => new TaskParticipantDto(p.UserId, p.Name, p.EmployeeId, p.Role,
            views.GetValueOrDefault(p.UserId)?.FirstSeenAt, views.GetValueOrDefault(p.UserId)?.LastSeenAt)).ToList();
    }

    public async Task<IReadOnlyList<TaskHistoryDto>> GetHistoryAsync(int id, CancellationToken ct = default)
    {
        await LoadVisibleAsync(id, ct, includeAssignments: false);
        return await db.TaskHistory.AsNoTracking().Where(h => h.TaskId == id).OrderByDescending(h => h.CreatedAt).ThenByDescending(h => h.Id)
            .Select(h => new TaskHistoryDto(h.Id, h.Action, h.FromStatus, h.ToStatus, h.Details, h.ChangedBy != null ? h.ChangedBy.FullName : "System", h.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<TaskAttachmentDto> AddAttachmentAsync(int id, string fileName, string contentType, long size, Stream content, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) throw new BadRequestException($"File type '{ext}' is not allowed.");
        if (size <= 0 || size > MaxAttachmentBytes) throw new BadRequestException("Files must be between 1 byte and 10 MB.");

        var stored = await storage.SaveAsync(content, ext, ct);
        var safeName = Path.GetFileName(fileName).Replace("\"", "");
        var att = new TaskAttachment
        {
            TaskId = id, UploadedByUserId = currentUser.UserId!.Value, FileName = safeName.Length > 255 ? safeName[^255..] : safeName,
            StoredFileName = stored, ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType, SizeBytes = size
        };
        db.TaskAttachments.Add(att);
        db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "AttachmentAdded", Details = att.FileName });
        try { await db.SaveChangesAsync(ct); }
        catch { storage.Delete(stored); throw; }
        var name = await db.Users.Where(u => u.Id == att.UploadedByUserId).Select(u => u.FullName).FirstAsync(ct);
        return new TaskAttachmentDto(att.Id, att.FileName, att.ContentType, att.SizeBytes, att.UploadedByUserId, name, att.CreatedAt);
    }

    public async Task<(TaskAttachment Attachment, Stream Content)> OpenAttachmentAsync(int id, int attachmentId, CancellationToken ct = default)
    {
        await LoadVisibleAsync(id, ct, includeAssignments: false);
        var att = await db.TaskAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId && a.TaskId == id, ct)
                  ?? throw new NotFoundException("Attachment", attachmentId);
        return (att, storage.OpenRead(att.StoredFileName));
    }

    public async Task DeleteAttachmentAsync(int id, int attachmentId, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        var att = await db.TaskAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId && a.TaskId == id, ct)
                  ?? throw new NotFoundException("Attachment", attachmentId);
        if (att.UploadedByUserId != currentUser.UserId && !await IsReviewerAsync(task, ct)) throw new ForbiddenException();
        db.TaskAttachments.Remove(att);
        db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "AttachmentRemoved", Details = att.FileName });
        await db.SaveChangesAsync(ct);
        storage.Delete(att.StoredFileName);
    }

    public async Task<TaskDashboardDto> GetDashboardAsync(bool mine, CancellationToken ct = default)
    {
        var query = await FilterAsync(new TaskQuery { Mine = mine }, ct);
        var now = clock.UtcNow;
        var weekEnd = now.AddDays(7);
        var counts = await query.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var overdue = await query.CountAsync(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate != null && t.DueDate < now, ct);
        var dueWeek = await query.CountAsync(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate >= now && t.DueDate <= weekEnd, ct);
        var byPriority = await query.Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled)
            .GroupBy(t => t.Priority).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int C(TaskItemStatus s) => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0;
        return new TaskDashboardDto(counts.Sum(c => c.Count), C(TaskItemStatus.New), C(TaskItemStatus.Assigned), C(TaskItemStatus.InProgress),
            C(TaskItemStatus.PendingReview), C(TaskItemStatus.Completed), C(TaskItemStatus.Rejected), C(TaskItemStatus.Cancelled), overdue, dueWeek,
            Enum.GetValues<TaskPriority>().ToDictionary(p => p.ToString(), p => byPriority.FirstOrDefault(b => b.Key == p)?.Count ?? 0));
    }

    public async Task<IReadOnlyDictionary<int, ProjectTaskStats>> GetProjectStatsAsync(int? projectId, CancellationToken ct = default)
    {
        var query = db.Tasks.AsNoTracking().Where(t => t.ProjectId != null);
        if (projectId is int id) query = query.Where(t => t.ProjectId == id);
        var now = clock.UtcNow;
        var rows = await query.GroupBy(t => t.ProjectId!.Value).Select(g => new
        {
            g.Key,
            Total = g.Count(),
            Completed = g.Count(t => t.Status == TaskItemStatus.Completed),
            Overdue = g.Count(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate != null && t.DueDate < now),
            Progress = g.Where(t => t.Status != TaskItemStatus.Cancelled).Average(t => (double?)t.ProgressPercent),
        }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => new ProjectTaskStats(r.Total, r.Completed, r.Overdue, (int)Math.Round(r.Progress ?? 0)));
    }

    public async Task<int[]?> AccessibleProjectIdsAsync(CancellationToken ct = default)
    {
        if (currentUser.HasPermission(Permissions.TasksViewAll) || currentUser.HasPermission(Permissions.ProjectsManage)) return null;
        return await (await VisibleTasksAsync(ct)).Where(t => t.ProjectId != null).Select(t => t.ProjectId!.Value).Distinct().ToArrayAsync(ct);
    }

    public async Task<(IReadOnlyList<TaskDto> Tasks, int[] OpenableIds)> GetProjectTasksAsync(int projectId, CancellationToken ct = default)
    {
        var tasks = await db.Tasks.AsNoTracking().Where(t => t.ProjectId == projectId).Select(Projection(clock.UtcNow)).Take(5_000).ToListAsync(ct);
        var openable = await (await VisibleTasksAsync(ct)).Where(t => t.ProjectId == projectId).Select(t => t.Id).ToArrayAsync(ct);
        return (tasks, openable);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>New links only to open projects; a task may keep the (now closed) project it already had.</summary>
    private async Task<int?> ValidateProjectAsync(int? projectId, int? currentProjectId, CancellationToken ct)
    {
        if (projectId is not int id) return null;
        var status = await db.Projects.Where(p => p.Id == id).Select(p => (ProjectStatus?)p.Status).FirstOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Project", id);
        if (id != currentProjectId && status is ProjectStatus.Completed or ProjectStatus.Cancelled)
            throw new BadRequestException("Tasks cannot be added to a completed or cancelled project.");
        return id;
    }


    private record AssigneeInfo(int Id, string Name, int DepartmentId, int? UserId);

    private async Task<List<AssigneeInfo>> ValidateAssigneesAsync(int[] ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0) return [];
        var scoped = await scope.TaskScopedEmployeesAsync(ct);
        var found = await db.Employees.Where(e => distinct.Contains(e.Id) && !e.IsDeleted && e.IsActive)
            .Select(e => new AssigneeInfo(e.Id, e.FirstName + " " + e.LastName, e.DepartmentId, e.UserId)).ToListAsync(ct);
        if (found.Count != distinct.Length) throw new BadRequestException("One or more assignees do not exist or are inactive.");
        if (!currentUser.HasPermission(Permissions.TasksViewAll))
        {
            var allowed = await scoped.CountAsync(e => distinct.Contains(e.Id), ct);
            if (allowed != distinct.Length) throw new ForbiddenException("You can only assign tasks to members of the departments you head or the teams you lead.");
        }
        // Preserve requested order (first = primary assignee).
        return distinct.Select(id => found.First(f => f.Id == id)).ToList();
    }

    /// <summary>
    /// Departments and teams the current user may give tasks to (null = any). Task admins: everything. Others: only the
    /// departments they head and the teams they lead or that belong to a department they head.
    /// </summary>
    private async Task<(int[]? Departments, int[]? Teams)> AllowedTargetsAsync(CancellationToken ct)
    {
        if (currentUser.HasPermission(Permissions.TasksViewAll)) return (null, null);
        var me = await scope.GetCurrentEmployeeIdAsync(ct) ?? -1;
        var departments = await db.Departments.Where(d => d.ManagerEmployeeId == me).Select(d => d.Id).ToArrayAsync(ct);
        var teams = await db.Teams.Where(t => t.LeaderEmployeeId == me || t.Department.ManagerEmployeeId == me).Select(t => t.Id).ToArrayAsync(ct);
        return (departments, teams);
    }

    public async Task<TaskTargetsDto> GetTargetsAsync(CancellationToken ct = default)
    {
        if (!currentUser.HasPermission(Permissions.TasksAssign)) throw new ForbiddenException("You cannot assign tasks.");
        var (deptIds, teamIds) = await AllowedTargetsAsync(ct);

        var departments = db.Departments.Where(d => d.IsActive);
        if (deptIds is not null) departments = departments.Where(d => deptIds.Contains(d.Id));
        var teams = db.Teams.Where(t => t.IsActive);
        if (teamIds is not null) teams = teams.Where(t => teamIds.Contains(t.Id));

        return new TaskTargetsDto(
            await departments.OrderBy(d => d.Name).Select(d => new TaskTargetDepartmentDto(d.Id, d.Name, d.NameAr, d.ManagerEmployeeId)).ToListAsync(ct),
            await teams.OrderBy(t => t.Name).Select(t => new TaskTargetTeamDto(t.Id, t.Name, t.NameAr, t.DepartmentId, t.LeaderEmployeeId)).ToListAsync(ct),
            await (await scope.TaskScopedEmployeesAsync(ct)).Where(e => e.IsActive)
                .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
                .Select(e => new TaskTargetEmployeeDto(e.Id, e.FirstName + " " + e.LastName, e.EmployeeCode, e.DepartmentId,
                    e.TeamMemberships.Select(m => m.TeamId).ToArray()))
                .ToListAsync(ct));
    }

    /// <summary>Validates the chosen department/team; a team implies its department. Neither → the first assignee's department.</summary>
    private async Task<(int? DepartmentId, int? TeamId)> ResolveTargetAsync(int? departmentId, int? teamId, List<AssigneeInfo> assignees,
        CancellationToken ct)
    {
        var (allowedDepartments, allowedTeams) = await AllowedTargetsAsync(ct);
        if (teamId is int t)
        {
            var teamDepartment = await db.Teams.Where(x => x.Id == t).Select(x => (int?)x.DepartmentId).FirstOrDefaultAsync(ct)
                                 ?? throw new NotFoundException("Team", t);
            if (allowedTeams is not null && !allowedTeams.Contains(t)) throw new ForbiddenException("You cannot create tasks for that team.");
            if (departmentId is int chosen && chosen != teamDepartment)
                throw new BadRequestException("The selected team does not belong to the selected department.");
            return (teamDepartment, t);
        }
        if (departmentId is int d)
        {
            if (allowedDepartments is not null && !allowedDepartments.Contains(d)) throw new ForbiddenException("You cannot create tasks for that department.");
            if (!await db.Departments.AnyAsync(x => x.Id == d, ct)) throw new NotFoundException("Department", d);
            return (d, null);
        }
        return (assignees.FirstOrDefault()?.DepartmentId, null);
    }

    private void NotifyAssigned(TaskItem task, IEnumerable<int> employeeIds)
    {
        var due = task.DueDate is DateTime d ? $" Due {d:yyyy-MM-dd HH:mm} UTC." : "";
        notifications.AddForEmployees(employeeIds, NotificationType.TaskAssigned, "New task assigned",
            $"You were assigned \"{task.Title}\" ({task.Priority}).{due}",
            "مهمة جديدة", $"تم إسناد المهمة \"{task.Title}\" إليك.", $"/tasks/{task.Id}", "Task", task.Id);
    }

    private async Task<List<int>> RelatedUserIdsAsync(TaskItem task, CancellationToken ct)
    {
        var empIds = task.Assignments.Select(a => a.EmployeeId).ToList();
        var users = await db.Employees.Where(e => empIds.Contains(e.Id) && e.UserId != null).Select(e => e.UserId!.Value).ToListAsync(ct);
        users.Add(task.AssignedByUserId);
        return users.Distinct().ToList();
    }

    private async Task NotifyStatusChangeAsync(TaskItem task, TaskItemStatus from, UpdateTaskStatusRequest r, CancellationToken ct)
    {
        var assigneeIds = task.Assignments.Select(a => a.EmployeeId).ToList();
        switch (r.Status)
        {
            case TaskItemStatus.PendingReview:
                // The creator and the task's department head / team leader must approve or reject it.
                var reviewers = (await ParticipantsAsync(task, ct)).Where(p => p.Role is "Creator" or "Leader").Select(p => p.UserId)
                    .Append(task.AssignedByUserId).Distinct().Where(u => u != currentUser.UserId);
                foreach (var uid in reviewers)
                    notifications.Add(uid, NotificationType.TaskStatusChanged, "Task submitted for review",
                        $"\"{task.Title}\" was submitted for review by {currentUser.UserName}. Please approve (with a rating) or reject it.",
                        "مهمة بانتظار المراجعة", $"تم تقديم المهمة \"{task.Title}\" للمراجعة. يرجى الموافقة عليها مع التقييم أو رفضها.",
                        $"/tasks/{task.Id}", "Task", task.Id);
                break;
            case TaskItemStatus.Completed:
                notifications.AddForEmployees(assigneeIds, NotificationType.TaskApproved, "Task approved",
                    $"\"{task.Title}\" was approved and marked completed. Rating: {r.Rating}/10.", "تمت الموافقة على المهمة",
                    $"تمت الموافقة على المهمة \"{task.Title}\". التقييم: {r.Rating}/10.", $"/tasks/{task.Id}", "Task", task.Id);
                break;
            case TaskItemStatus.Rejected:
                notifications.AddForEmployees(assigneeIds, NotificationType.TaskRejected, "Task rejected",
                    $"\"{task.Title}\" was rejected: {r.Note}", "تم رفض المهمة", $"تم رفض المهمة \"{task.Title}\": {r.Note}",
                    $"/tasks/{task.Id}", "Task", task.Id);
                break;
            default:
                var users = await RelatedUserIdsAsync(task, ct);
                foreach (var uid in users.Where(u => u != currentUser.UserId))
                    notifications.Add(uid, NotificationType.TaskStatusChanged, "Task status changed",
                        $"\"{task.Title}\" changed from {from} to {r.Status}.", "تغيرت حالة المهمة",
                        $"تغيرت حالة المهمة \"{task.Title}\".", $"/tasks/{task.Id}", "Task", task.Id);
                break;
        }
    }
}
