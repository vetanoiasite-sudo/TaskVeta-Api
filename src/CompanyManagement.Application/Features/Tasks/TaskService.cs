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
    /// <summary>Finds a task by its key (e.g. GA-123 or #45).</summary>
    Task<TaskDetailsDto> GetByKeyAsync(string key, CancellationToken ct = default);
    Task<TaskDetailsDto> CreateAsync(CreateTaskRequest request, CancellationToken ct = default);
    Task<TaskDetailsDto> UpdateAsync(int id, UpdateTaskRequest request, CancellationToken ct = default);
    Task<TaskDetailsDto> ChangeStatusAsync(int id, UpdateTaskStatusRequest request, CancellationToken ct = default);
    Task<TaskDetailsDto> UpdateProgressAsync(int id, UpdateTaskProgressRequest request, CancellationToken ct = default);
    Task<TaskDetailsDto> SetBlockedAsync(int id, SetTaskBlockedRequest request, CancellationToken ct = default);
    Task<TaskDetailsDto> SetSprintAsync(int id, SetTaskSprintRequest request, CancellationToken ct = default);
    Task<TaskDetailsDto> AddDependencyAsync(int id, AddDependencyRequest request, CancellationToken ct = default);
    Task<TaskDetailsDto> RemoveDependencyAsync(int id, int blockedByTaskId, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<TaskCommentDto> AddCommentAsync(int id, AddCommentRequest request, CancellationToken ct = default);
    /// <summary>Adds or removes the current user's emoji on a comment; returns the comment's reactions.</summary>
    Task<IReadOnlyList<TaskReactionDto>> ToggleReactionAsync(int id, int commentId, ToggleReactionRequest request, CancellationToken ct = default);
    /// <summary>Records that the current user opened the task (read receipt).</summary>
    Task MarkSeenAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<TaskHistoryDto>> GetHistoryAsync(int id, CancellationToken ct = default);
    Task<TaskAttachmentDto> AddAttachmentAsync(int id, string fileName, string contentType, long size, Stream content, int? durationSeconds = null,
        CancellationToken ct = default);
    Task<(TaskAttachment Attachment, Stream Content)> OpenAttachmentAsync(int id, int attachmentId, CancellationToken ct = default);
    Task DeleteAttachmentAsync(int id, int attachmentId, CancellationToken ct = default);
    Task<TaskDashboardDto> GetDashboardAsync(bool mine, CancellationToken ct = default);
    /// <param name="projectId">When the user leads this project, its members are added to the assignable employees.</param>
    Task<TaskTargetsDto> GetTargetsAsync(int? projectId = null, CancellationToken ct = default);
    /// <summary>Labels already used on visible tasks (for autocomplete).</summary>
    Task<IReadOnlyList<string>> GetLabelsAsync(CancellationToken ct = default);
    /// <summary>Task figures per project over all of its tasks.</summary>
    Task<IReadOnlyDictionary<int, ProjectTaskStats>> GetProjectStatsAsync(int? projectId, CancellationToken ct = default);
    /// <summary>Projects the current user may open: null = all (task admins / project managers); otherwise member projects, projects they created and projects with a task they can see.</summary>
    Task<int[]?> AccessibleProjectIdsAsync(CancellationToken ct = default);
    /// <summary>All tasks of a project (for its timeline) plus the ids of those the current user may open.</summary>
    Task<(IReadOnlyList<TaskDto> Tasks, int[] OpenableIds)> GetProjectTasksAsync(int projectId, CancellationToken ct = default);
    /// <summary>Visible tasks of a sprint (for the sprint report).</summary>
    Task<IReadOnlyList<TaskDto>> GetSprintTasksAsync(int sprintId, CancellationToken ct = default);
}

public class TaskService(
    IApplicationDbContext db,
    IAccessScope scope,
    IProjectAccess projectAccess,
    ICurrentUser currentUser,
    ICompanyClock clock,
    IAuditService audit,
    INotificationService notifications,
    IFileStorage storage) : ITaskService
{
    /// <summary>Request limit for uploads; the real limit depends on the kind of file (see <see cref="AttachmentRules"/>).</summary>
    public const long MaxAttachmentBytes = AttachmentRules.MaxBytes;

    private static readonly Expression<Func<TaskAttachment, TaskAttachmentDto>> AttachmentProjection = a => new TaskAttachmentDto(
        a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedByUserId, a.UploadedBy.FullName, a.CreatedAt,
        AttachmentRules.KindOfStored(a.FileName, a.ContentType), a.CommentId, a.DurationSeconds);

    private static readonly Dictionary<string, Expression<Func<TaskItem, object?>>> Sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = t => t.Title,
        ["priority"] = t => t.Priority,
        ["status"] = t => t.Status,
        ["type"] = t => t.Type,
        ["key"] = t => t.Number,
        ["estimateDays"] = t => t.EstimateDays,
        ["dueDate"] = t => t.DueDate,
        ["startDate"] = t => t.StartDate,
        ["progressPercent"] = t => t.ProgressPercent,
        ["createdAt"] = t => t.CreatedAt,
        ["updatedAt"] = t => t.UpdatedAt,
    };

    private static Expression<Func<TaskItem, TaskDto>> Projection(DateTime now) => t => new TaskDto(
        t.Id,
        t.Project != null && t.Number != null ? t.Project.Code + "-" + t.Number.Value.ToString() : "#" + t.Id.ToString(),
        t.Title, t.Type, t.Priority, t.Status, t.ProgressPercent, t.StartDate, t.DueDate, t.CompletionDate,
        t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate != null && t.DueDate < now,
        t.AssignedByUserId, t.AssignedBy.FullName, t.DepartmentId, t.Department != null ? t.Department.Name : null,
        t.TeamId, t.Team != null ? t.Team.Name : null, t.ProjectId, t.Project != null ? t.Project.Name : null,
        t.Project != null ? t.Project.Code : null, t.SprintId, t.Sprint != null ? t.Sprint.Name : null, t.ParentTaskId,
        t.EstimateDays, t.Labels, t.StoryRef, t.ScreenRef, t.IsBlocked, t.BlockedReason,
        t.Subtasks.Count(), t.Subtasks.Count(s => s.Status == TaskItemStatus.Completed),
        t.BlockedBy.Count(d => d.BlockedByTask.Status != TaskItemStatus.Completed && d.BlockedByTask.Status != TaskItemStatus.Cancelled),
        t.Assignments.Select(a => new TaskAssigneeDto(a.EmployeeId, a.Employee.EmployeeCode, a.Employee.FirstName + " " + a.Employee.LastName)).ToList(),
        t.Comments.Count, t.Attachments.Count, t.CreatedAt, t.UpdatedAt, t.Rating);

    private static readonly Expression<Func<TaskItem, TaskRefDto>> RefProjection = t => new TaskRefDto(
        t.Id, t.Project != null && t.Number != null ? t.Project.Code + "-" + t.Number.Value.ToString() : "#" + t.Id.ToString(),
        t.Title, t.Type, t.Status);

    // ------------------------------------------------------------------ keys

    /// <summary>Parses "GA-123" (project code may itself contain '-': the number is after the last one) or "#45" / "45".</summary>
    public static bool TryParseKey(string? key, out string? projectCode, out int number)
    {
        projectCode = null;
        number = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim();
        if (k.StartsWith('#')) return int.TryParse(k[1..], out number) && number > 0;
        var dash = k.LastIndexOf('-');
        if (dash <= 0) return int.TryParse(k, out number) && number > 0;
        if (!int.TryParse(k[(dash + 1)..], out number) || number <= 0) return false;
        projectCode = k[..dash].ToUpperInvariant();
        return true;
    }

    // ------------------------------------------------------------------ visibility

    private async Task<IQueryable<TaskItem>> VisibleTasksAsync(CancellationToken ct)
    {
        if (currentUser.HasPermission(Permissions.TasksViewAll)) return db.Tasks.AsQueryable();
        var userId = currentUser.UserId ?? -1;
        var me = await scope.GetCurrentEmployeeIdAsync(ct) ?? -1;
        // Project members see every task of their projects.
        var projects = await projectAccess.MemberProjectIdsAsync(ct);
        if (currentUser.HasPermission(Permissions.TasksAssign))
        {
            var scoped = (await scope.TaskScopedEmployeesAsync(ct)).Select(e => e.Id);
            return db.Tasks.Where(t => t.AssignedByUserId == userId || t.Assignments.Any(a => scoped.Contains(a.EmployeeId))
                                       || (t.ProjectId != null && projects.Contains(t.ProjectId.Value)));
        }
        return db.Tasks.Where(t => t.AssignedByUserId == userId || t.Assignments.Any(a => a.EmployeeId == me)
                                   || (t.ProjectId != null && projects.Contains(t.ProjectId.Value)));
    }

    private async Task<TaskItem> LoadVisibleAsync(int id, CancellationToken ct, bool includeAssignments = true)
    {
        var visible = await VisibleTasksAsync(ct);
        var q = includeAssignments ? visible.Include(t => t.Assignments) : visible;
        return await q.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Task", id);
    }

    /// <summary>
    /// Reviewer = full task admin, a lead of the task's project, the creator, or a manager whose scope covers every assignee.
    /// </summary>
    private async Task<bool> IsReviewerAsync(TaskItem task, CancellationToken ct)
    {
        if (currentUser.HasPermission(Permissions.TasksViewAll) && currentUser.HasPermission(Permissions.TasksReview)) return true;
        if (task.ProjectId is int p && await projectAccess.IsLeadAsync(p, ct)) return true;
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

    private async Task<bool> CanPlanAsync(TaskItem task, CancellationToken ct) =>
        currentUser.HasPermission(Permissions.SprintsManage) || await IsReviewerAsync(task, ct);

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
        if (q.Type is TaskType ty) query = query.Where(t => t.Type == ty);
        if (q.AssigneeId is int a) query = query.Where(t => t.Assignments.Any(x => x.EmployeeId == a));
        if (q.DepartmentId is int d) query = query.Where(t => t.DepartmentId == d);
        if (q.TeamId is int tm) query = query.Where(t => t.TeamId == tm);
        if (q.ProjectId is int pr) query = query.Where(t => t.ProjectId == pr);
        if (q.SprintId is int sp) query = query.Where(t => t.SprintId == sp);
        if (q.Backlog)
            query = query.Where(t => t.SprintId == null && t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled);
        if (q.ParentTaskId is int parent) query = query.Where(t => t.ParentTaskId == parent);
        if (q.TopLevelOnly) query = query.Where(t => t.ParentTaskId == null);
        if (!string.IsNullOrWhiteSpace(q.Label))
        {
            var label = NormalizeLabel(q.Label);
            query = query.Where(t => t.Labels.Contains(label));
        }
        if (q.Blocked is bool blocked) query = query.Where(t => t.IsBlocked == blocked);
        if (q.Overdue == true)
            query = query.Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.DueDate != null && t.DueDate < now);
        if (q.DueFrom is DateTime df) query = query.Where(t => t.DueDate >= df);
        if (q.DueTo is DateTime dt) query = query.Where(t => t.DueDate <= dt);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim().ToLower();
            if (TryParseKey(q.Search, out var code, out var number))
            {
                // "GA-12" → that task; "#12" / "12" → task id 12; plus normal text matches.
                query = code is not null
                    ? query.Where(t => (t.Project != null && t.Project.Code == code && t.Number == number)
                                       || t.Title.ToLower().Contains(term))
                    : query.Where(t => t.Id == number || t.Title.ToLower().Contains(term));
            }
            else
            {
                query = query.Where(t => t.Title.ToLower().Contains(term) || (t.Description != null && t.Description.ToLower().Contains(term))
                                         || (t.StoryRef != null && t.StoryRef.ToLower() == term)
                                         || (t.ScreenRef != null && t.ScreenRef.ToLower() == term));
            }
        }
        return query;
    }

    public async Task<TaskDetailsDto> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        if (!TryParseKey(key, out var code, out var number)) throw new BadRequestException($"'{key}' is not a task key (e.g. GA-123).");
        int? id = code is null
            ? number
            : await db.Tasks.Where(t => t.Project != null && t.Project.Code == code && t.Number == number).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
        if (id is not int found) throw new NotFoundException("Task", key);
        return await GetAsync(found, ct);
    }

    public async Task<TaskDetailsDto> GetAsync(int id, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        var now = clock.UtcNow;
        var dto = await db.Tasks.AsNoTracking().Where(t => t.Id == id).Select(Projection(now)).FirstAsync(ct);
        var attachments = await db.TaskAttachments.AsNoTracking().Where(a => a.TaskId == id).OrderBy(a => a.CreatedAt)
            .Select(AttachmentProjection).ToListAsync(ct);
        var byComment = attachments.Where(a => a.CommentId != null).ToLookup(a => a.CommentId!.Value);
        var reactions = await ReactionsAsync(db.TaskCommentReactions.Where(r => r.Comment.TaskId == id), ct);
        var comments = (await db.TaskComments.AsNoTracking().Where(c => c.TaskId == id).OrderBy(c => c.CreatedAt)
                .Select(c => new { c.Id, c.UserId, UserName = c.User.FullName, c.Content, c.CreatedAt, c.MentionedUserIds }).ToListAsync(ct))
            .Select(c => new TaskCommentDto(c.Id, c.UserId, c.UserName, c.Content, c.CreatedAt, c.MentionedUserIds, byComment[c.Id].ToList(),
                reactions.GetValueOrDefault(c.Id) ?? []))
            .ToList();

        var parent = task.ParentTaskId is int pid
            ? await db.Tasks.AsNoTracking().Where(t => t.Id == pid).Select(RefProjection).FirstOrDefaultAsync(ct)
            : null;
        var subtasks = await db.Tasks.AsNoTracking().Where(t => t.ParentTaskId == id).OrderBy(t => t.Id).Select(RefProjection).ToListAsync(ct);
        var blockedBy = await db.TaskDependencies.AsNoTracking().Where(d => d.TaskId == id).Select(d => d.BlockedByTask)
            .OrderBy(t => t.Id).Select(RefProjection).ToListAsync(ct);
        var blocks = await db.TaskDependencies.AsNoTracking().Where(d => d.BlockedByTaskId == id).Select(d => d.Task)
            .OrderBy(t => t.Id).Select(RefProjection).ToListAsync(ct);

        var reviewer = await IsReviewerAsync(task, ct);
        var assignee = await IsAssigneeAsync(task, ct);
        var allowed = Enum.GetValues<TaskItemStatus>().Where(s => TaskWorkflow.CanTransition(task.Status, s, reviewer, assignee)).ToArray();
        var perms = new TaskPermissionsDto(reviewer, reviewer, (assignee || reviewer) && !task.IsClosed, true, allowed,
            (assignee || reviewer) && !task.IsClosed, await CanPlanAsync(task, ct));
        var participants = await ParticipantsAsync(task, ct);
        var reviews = await db.TaskReviews.AsNoTracking().Where(r => r.TaskId == id).OrderByDescending(r => r.CreatedAt)
            .Select(r => new TaskReviewDto(r.Id, r.Approved, r.Rating, r.Notes, r.ReviewerUserId, r.Reviewer.FullName, r.CreatedAt)).ToListAsync(ct);
        return new TaskDetailsDto(dto, task.Description, task.ReviewNotes, task.DesignUrl, task.PullRequestUrl, parent, subtasks, blockedBy, blocks,
            comments, attachments, perms, task.RowVersion.ToString(), participants, reviews);
    }

    public async Task<IReadOnlyList<string>> GetLabelsAsync(CancellationToken ct = default)
    {
        var lists = await (await VisibleTasksAsync(ct)).AsNoTracking().Where(t => t.Labels.Length > 0).OrderByDescending(t => t.Id).Select(t => t.Labels).Take(5_000).ToListAsync(ct);
        return lists.SelectMany(l => l).Distinct().Order().ToList();
    }

    // ------------------------------------------------------------------ commands

    public async Task<TaskDetailsDto> CreateAsync(CreateTaskRequest r, CancellationToken ct = default)
    {
        var leadsProject = r.ProjectId is int rp && await projectAccess.IsLeadAsync(rp, ct);
        if (!currentUser.HasPermission(Permissions.TasksAssign) && !leadsProject) throw new ForbiddenException("You cannot assign tasks.");
        var assignees = await ValidateAssigneesAsync(r.AssigneeEmployeeIds, leadsProject ? r.ProjectId : null, ct);
        var (departmentId, teamId) = await ResolveTargetAsync(r.DepartmentId, r.TeamId, assignees, ct);
        var projectId = await ValidateProjectAsync(r.ProjectId, null, ct);
        var parentId = await ValidateParentAsync(r.ParentTaskId, null, projectId, ct);
        var sprintId = await ValidateSprintAsync(r.SprintId, null, ct);

        var task = new TaskItem
        {
            Title = r.Title.Trim(), Description = r.Description, Priority = r.Priority, StartDate = r.StartDate, DueDate = r.DueDate,
            DepartmentId = departmentId, TeamId = teamId, ProjectId = projectId, AssignedByUserId = currentUser.UserId!.Value,
            AssignedToEmployeeId = assignees.FirstOrDefault()?.Id,
            Status = assignees.Count > 0 ? TaskItemStatus.Assigned : TaskItemStatus.New,
            Type = r.Type, SprintId = sprintId, ParentTaskId = parentId, EstimateDays = r.EstimateDays,
        };
        ApplyPlanningFields(task, r.Labels, r.StoryRef, r.ScreenRef, r.DesignUrl, r.PullRequestUrl);
        foreach (var e in assignees)
            task.Assignments.Add(new TaskAssignment { EmployeeId = e.Id, AssignedByUserId = task.AssignedByUserId });
        task.History.Add(new TaskHistory
        {
            ChangedByUserId = currentUser.UserId, Action = "Created", ToStatus = task.Status,
            Details = assignees.Count > 0 ? $"Assigned to {string.Join(", ", assignees.Select(a => a.Name))}" : null
        });
        db.Tasks.Add(task);
        await SaveWithNumberAsync(task, ct);

        audit.Log("Create", "Task", task.Id, newValues: new
        {
            r.Title, r.Type, r.Priority, r.DueDate, departmentId, teamId, projectId, task.Number, sprintId, parentId, Assignees = r.AssigneeEmployeeIds
        });
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

        var leadsTarget = r.ProjectId is int rp && await projectAccess.IsLeadAsync(rp, ct);
        var assignees = await ValidateAssigneesAsync(r.AssigneeEmployeeIds, leadsTarget ? r.ProjectId : null, ct,
            alreadyAssigned: task.Assignments.Select(a => a.EmployeeId).ToHashSet());
        var old = new
        {
            task.Title, task.Type, task.Priority, task.DueDate, task.StartDate, task.DepartmentId, task.TeamId, task.ProjectId, task.SprintId,
            task.ParentTaskId, task.EstimateDays, Assignees = task.Assignments.Select(a => a.EmployeeId).ToArray()
        };

        task.Title = r.Title.Trim();
        task.Description = r.Description;
        task.Priority = r.Priority;
        task.Type = r.Type;
        task.StartDate = r.StartDate;
        if (task.DueDate != r.DueDate) { task.DeadlineReminderSentAt = null; task.OverdueNotifiedAt = null; }
        task.DueDate = r.DueDate;
        (task.DepartmentId, task.TeamId) = await ResolveTargetAsync(r.DepartmentId, r.TeamId, assignees, ct);
        task.ProjectId = await ValidateProjectAsync(r.ProjectId, old.ProjectId, ct);
        if (task.ProjectId != old.ProjectId && r.ParentTaskId is null && await db.Tasks.AnyAsync(t => t.ParentTaskId == id, ct))
            throw new BadRequestException("Move the sub-tasks first: a task and its sub-tasks stay in the same project.");
        task.ParentTaskId = await ValidateParentAsync(r.ParentTaskId, id, task.ProjectId, ct);
        task.SprintId = await ValidateSprintAsync(r.SprintId, old.SprintId, ct);
        task.EstimateDays = r.EstimateDays;
        ApplyPlanningFields(task, r.Labels, r.StoryRef, r.ScreenRef, r.DesignUrl, r.PullRequestUrl);
        if (task.ProjectId != old.ProjectId) task.Number = null; // a new number in the new project is given on save

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
        if (old.Type != task.Type) details.Add($"type → {task.Type}");
        if (old.Priority != task.Priority) details.Add($"priority → {task.Priority}");
        if (old.DueDate != task.DueDate) details.Add($"due date → {task.DueDate:yyyy-MM-dd HH:mm}");
        if (old.ProjectId != task.ProjectId) details.Add("project");
        if (old.SprintId != task.SprintId) details.Add(task.SprintId is null ? "moved to backlog" : "sprint");
        if (old.ParentTaskId != task.ParentTaskId) details.Add("parent task");
        if (old.EstimateDays != task.EstimateDays) details.Add($"estimate → {task.EstimateDays?.ToString("0.#") ?? "none"}");
        if (added.Count > 0) details.Add($"added {added.Count} assignee(s)");
        if (removed.Count > 0) details.Add($"removed {removed.Count} assignee(s)");
        db.TaskHistory.Add(new TaskHistory
        {
            TaskId = id, ChangedByUserId = currentUser.UserId, Action = "Updated",
            Details = details.Count > 0 ? "Changed " + string.Join(", ", details) : "Details updated"
        });
        audit.Log("Update", "Task", id, old, new
        {
            r.Title, r.Type, r.Priority, r.DueDate, r.StartDate, task.DepartmentId, task.TeamId, task.ProjectId, task.SprintId, task.ParentTaskId,
            r.EstimateDays, Assignees = r.AssigneeEmployeeIds
        });
        NotifyAssigned(task, added);

        try { await SaveWithNumberAsync(task, ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The task was modified by someone else. Reload and try again."); }
        await notifications.SaveAndDispatchAsync(ct);
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
        // A finished task is no longer waiting for anything.
        if (r.Status is TaskItemStatus.Completed or TaskItemStatus.Cancelled) ClearBlocked(task);
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
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The task was modified by someone else. Reload and try again."); }
        return await GetAsync(id, ct);
    }

    public async Task<TaskDetailsDto> SetBlockedAsync(int id, SetTaskBlockedRequest r, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        if (!await IsAssigneeAsync(task, ct) && !await IsReviewerAsync(task, ct)) throw new ForbiddenException("You cannot update this task.");
        if (r.IsBlocked && task.IsClosed) throw new ConflictException("Closed tasks cannot be blocked.");
        if (r.IsBlocked == task.IsBlocked && (!r.IsBlocked || r.Reason?.Trim() == task.BlockedReason)) return await GetAsync(id, ct);

        if (r.IsBlocked)
        {
            task.IsBlocked = true;
            task.BlockedReason = r.Reason!.Trim();
            task.BlockedAt = clock.UtcNow;
            db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "Blocked", Details = task.BlockedReason });
            // Tell the people who can unblock it: the creator, the department head / team leader and the project leads.
            var recipients = (await ParticipantsAsync(task, ct)).Where(p => p.Role is "Creator" or "Leader" or "ProjectLead")
                .Select(p => p.UserId).Distinct().Where(u => u != currentUser.UserId);
            foreach (var uid in recipients)
                notifications.Add(uid, NotificationType.TaskBlocked, "Task blocked",
                    $"\"{task.Title}\" is blocked: {task.BlockedReason}", "مهمة متوقفة",
                    $"المهمة \"{task.Title}\" متوقفة: {task.BlockedReason}", $"/tasks/{id}", "Task", id);
        }
        else
        {
            ClearBlocked(task);
            db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "Unblocked", Details = r.Reason });
        }
        audit.Log(r.IsBlocked ? "Block" : "Unblock", "Task", id, newValues: new { r.IsBlocked, r.Reason });
        try { await notifications.SaveAndDispatchAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The task was modified by someone else. Reload and try again."); }
        return await GetAsync(id, ct);
    }

    private static void ClearBlocked(TaskItem task)
    {
        task.IsBlocked = false;
        task.BlockedReason = null;
        task.BlockedAt = null;
    }

    public async Task<TaskDetailsDto> SetSprintAsync(int id, SetTaskSprintRequest r, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        if (!await CanPlanAsync(task, ct)) throw new ForbiddenException("You cannot plan this task.");
        if (task.SprintId == r.SprintId) return await GetAsync(id, ct);
        var from = task.SprintId;
        task.SprintId = await ValidateSprintAsync(r.SprintId, from, ct);
        var name = r.SprintId is int s ? await db.Sprints.Where(x => x.Id == s).Select(x => x.Name).FirstAsync(ct) : null;
        db.TaskHistory.Add(new TaskHistory
        {
            TaskId = id, ChangedByUserId = currentUser.UserId, Action = "SprintChanged",
            Details = name is null ? "Moved to backlog" : $"Planned in {name}"
        });
        audit.Log("ChangeSprint", "Task", id, new { SprintId = from }, new { r.SprintId });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The task was modified by someone else. Reload and try again."); }
        return await GetAsync(id, ct);
    }

    public async Task<TaskDetailsDto> AddDependencyAsync(int id, AddDependencyRequest r, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        if (!await IsReviewerAsync(task, ct)) throw new ForbiddenException("You cannot change this task's dependencies.");
        if (r.BlockedByTaskId == id) throw new BadRequestException("A task cannot depend on itself.");
        var blocker = await LoadVisibleAsync(r.BlockedByTaskId, ct, includeAssignments: false);
        if (await db.TaskDependencies.AnyAsync(d => d.TaskId == id && d.BlockedByTaskId == blocker.Id, ct)) return await GetAsync(id, ct);
        if (await DependsOnAsync(blocker.Id, id, ct))
            throw new BadRequestException("That would create a circular dependency.");

        db.TaskDependencies.Add(new TaskDependency { TaskId = id, BlockedByTaskId = blocker.Id });
        db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "DependencyAdded", Details = $"Waits for \"{blocker.Title}\"" });
        audit.Log("AddDependency", "Task", id, newValues: new { r.BlockedByTaskId });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<TaskDetailsDto> RemoveDependencyAsync(int id, int blockedByTaskId, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        if (!await IsReviewerAsync(task, ct)) throw new ForbiddenException("You cannot change this task's dependencies.");
        var dep = await db.TaskDependencies.Include(d => d.BlockedByTask).FirstOrDefaultAsync(d => d.TaskId == id && d.BlockedByTaskId == blockedByTaskId, ct)
                  ?? throw new NotFoundException("Dependency", blockedByTaskId);
        db.TaskDependencies.Remove(dep);
        db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "DependencyRemoved", Details = $"No longer waits for \"{dep.BlockedByTask.Title}\"" });
        audit.Log("RemoveDependency", "Task", id, new { BlockedByTaskId = blockedByTaskId });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>True when <paramref name="taskId"/> (transitively) waits for <paramref name="target"/>.</summary>
    private async Task<bool> DependsOnAsync(int taskId, int target, CancellationToken ct)
    {
        var seen = new HashSet<int> { taskId };
        var frontier = new List<int> { taskId };
        while (frontier.Count > 0)
        {
            var next = await db.TaskDependencies.AsNoTracking().Where(d => frontier.Contains(d.TaskId)).Select(d => d.BlockedByTaskId).ToListAsync(ct);
            if (next.Contains(target)) return true;
            frontier = next.Where(seen.Add).ToList();
        }
        return false;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var task = await LoadVisibleAsync(id, ct);
        if (!await IsReviewerAsync(task, ct)) throw new ForbiddenException("You cannot delete this task.");
        var files = await db.TaskAttachments.Where(a => a.TaskId == id).Select(a => a.StoredFileName).ToListAsync(ct);
        db.Tasks.Remove(task); // cascades to assignments/comments/attachments/history/dependencies; sub-tasks become top-level
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

        // Files sent with the comment: uploaded by me to this task and not sent with another comment yet.
        var files = new List<TaskAttachment>();
        if (r.AttachmentIds is { Length: > 0 } ids)
        {
            var wanted = ids.Distinct().ToArray();
            files = await db.TaskAttachments.Include(a => a.UploadedBy)
                .Where(a => wanted.Contains(a.Id) && a.TaskId == id && a.UploadedByUserId == me && a.CommentId == null).ToListAsync(ct);
            if (files.Count != wanted.Length) throw new BadRequestException("Some files cannot be attached to this comment.");
        }
        var comment = new TaskComment { TaskId = id, UserId = me, Content = (r.Content ?? "").Trim(), MentionedUserIds = mentioned };
        foreach (var f in files) comment.Attachments.Add(f);
        db.TaskComments.Add(comment);
        db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "Commented" });
        await TouchViewAsync(id, me, ct); // the author has obviously seen the task

        var name = await db.Users.Where(u => u.Id == me).Select(u => u.FullName).FirstAsync(ct);
        var preview = comment.Content.Length > 0 ? Excerpt(comment.Content)
            : files.Any(f => f.ContentType.StartsWith("audio/")) ? "\U0001F3A4 Voice note" : "\U0001F4CE File";
        var previewAr = comment.Content.Length > 0 ? Excerpt(comment.Content)
            : files.Any(f => f.ContentType.StartsWith("audio/")) ? "\U0001F3A4 رسالة صوتية" : "\U0001F4CE ملف";
        foreach (var uid in mentioned)
            notifications.Add(uid, NotificationType.TaskMention, "You were mentioned",
                $"{name} mentioned you in \"{task.Title}\": {preview}", "تمت الإشارة إليك",
                $"أشار إليك {name} في المهمة \"{task.Title}\": {previewAr}", $"/tasks/{id}", "Task", id);
        var recipients = await RelatedUserIdsAsync(task, ct);
        foreach (var uid in recipients.Where(u => u != me && !mentioned.Contains(u)))
            notifications.Add(uid, NotificationType.TaskComment, "New comment",
                $"{name} commented on \"{task.Title}\".", "تعليق جديد", $"علّق {name} على \"{task.Title}\".",
                $"/tasks/{id}", "Task", id);
        await notifications.SaveAndDispatchAsync(ct);
        var dtos = files.Select(f => new TaskAttachmentDto(f.Id, f.FileName, f.ContentType, f.SizeBytes, f.UploadedByUserId, f.UploadedBy.FullName,
            f.CreatedAt, AttachmentRules.KindOfStored(f.FileName, f.ContentType), comment.Id, f.DurationSeconds)).ToList();
        return new TaskCommentDto(comment.Id, comment.UserId, name, comment.Content, comment.CreatedAt, comment.MentionedUserIds, dtos, []);
    }

    public async Task<IReadOnlyList<TaskReactionDto>> ToggleReactionAsync(int id, int commentId, ToggleReactionRequest r, CancellationToken ct = default)
    {
        await LoadVisibleAsync(id, ct, includeAssignments: false);
        if (!await db.TaskComments.AnyAsync(c => c.Id == commentId && c.TaskId == id, ct)) throw new NotFoundException("Comment", commentId);
        var me = currentUser.UserId!.Value;
        var emoji = r.Emoji.Trim();
        var existing = await db.TaskCommentReactions.FirstOrDefaultAsync(x => x.CommentId == commentId && x.UserId == me && x.Emoji == emoji, ct);
        if (existing is not null) db.TaskCommentReactions.Remove(existing);
        else
        {
            if (await db.TaskCommentReactions.Where(x => x.CommentId == commentId && x.UserId == me).CountAsync(ct) >= 10)
                throw new BadRequestException("You can add at most 10 reactions to a comment.");
            db.TaskCommentReactions.Add(new TaskCommentReaction { CommentId = commentId, UserId = me, Emoji = emoji });
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* double click: the same reaction was added by the other request */ }
        return (await ReactionsAsync(db.TaskCommentReactions.Where(x => x.CommentId == commentId), ct)).GetValueOrDefault(commentId) ?? [];
    }

    /// <summary>Reactions grouped per comment, then per emoji in the order they were first used.</summary>
    private static async Task<Dictionary<int, List<TaskReactionDto>>> ReactionsAsync(IQueryable<TaskCommentReaction> query, CancellationToken ct)
    {
        var rows = await query.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.CommentId, x.Emoji, x.UserId, Name = x.User.FullName }).ToListAsync(ct);
        return rows.GroupBy(x => x.CommentId).ToDictionary(g => g.Key, g => g.GroupBy(x => x.Emoji)
            .Select(e => new TaskReactionDto(e.Key, e.Count(), e.Select(x => new TaskReactionUserDto(x.UserId, x.Name)).ToList())).ToList());
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
    /// People involved in the task, with read receipts: assignees (with accounts), the creator, the head of the task's
    /// department / leader of its team, and the leads of its project. A person is listed once, with their most specific role.
    /// </summary>
    private async Task<List<TaskParticipantDto>> ParticipantsAsync(TaskItem task, CancellationToken ct)
    {
        var assigneeIds = task.Assignments.Select(a => a.EmployeeId).ToList();
        var leaderEmployeeIds = new List<int>();
        if (task.DepartmentId is int d && await db.Departments.Where(x => x.Id == d).Select(x => x.ManagerEmployeeId).FirstOrDefaultAsync(ct) is int head)
            leaderEmployeeIds.Add(head);
        if (task.TeamId is int t && await db.Teams.Where(x => x.Id == t).Select(x => x.LeaderEmployeeId).FirstOrDefaultAsync(ct) is int lead)
            leaderEmployeeIds.Add(lead);
        var projectLeadIds = task.ProjectId is int p
            ? await db.ProjectMembers.Where(m => m.ProjectId == p && m.Role != ProjectRole.Member).Select(m => m.EmployeeId).Distinct().ToListAsync(ct)
            : [];

        var employeeIds = assigneeIds.Concat(leaderEmployeeIds).Concat(projectLeadIds).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id) && e.UserId != null && !e.IsDeleted)
            .Select(e => new { e.Id, UserId = e.UserId!.Value, Name = e.FirstName + " " + e.LastName }).ToListAsync(ct);

        var list = new List<(int UserId, string Name, int? EmployeeId, string Role)>();
        void Add(int userId, string name, int? employeeId, string role)
        {
            if (list.All(x => x.UserId != userId)) list.Add((userId, name, employeeId, role));
        }
        foreach (var id in assigneeIds)
            if (employees.FirstOrDefault(e => e.Id == id) is { } e) Add(e.UserId, e.Name, e.Id, "Assignee");
        var creator = await db.Users.AsNoTracking().Where(u => u.Id == task.AssignedByUserId).Select(u => new { u.Id, u.FullName }).FirstAsync(ct);
        var creatorEmployee = await db.Employees.Where(e => e.UserId == creator.Id).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        Add(creator.Id, creator.FullName, creatorEmployee, "Creator");
        foreach (var id in leaderEmployeeIds)
            if (employees.FirstOrDefault(e => e.Id == id) is { } e) Add(e.UserId, e.Name, e.Id, "Leader");
        foreach (var id in projectLeadIds)
            if (employees.FirstOrDefault(e => e.Id == id) is { } e) Add(e.UserId, e.Name, e.Id, "ProjectLead");

        var userIds = list.Select(x => x.UserId).ToList();
        var views = await db.TaskViews.AsNoTracking().Where(v => v.TaskId == task.Id && userIds.Contains(v.UserId))
            .ToDictionaryAsync(v => v.UserId, ct);
        return list.Select(x => new TaskParticipantDto(x.UserId, x.Name, x.EmployeeId, x.Role,
            views.GetValueOrDefault(x.UserId)?.FirstSeenAt, views.GetValueOrDefault(x.UserId)?.LastSeenAt)).ToList();
    }

    public async Task<IReadOnlyList<TaskHistoryDto>> GetHistoryAsync(int id, CancellationToken ct = default)
    {
        await LoadVisibleAsync(id, ct, includeAssignments: false);
        return await db.TaskHistory.AsNoTracking().Where(h => h.TaskId == id).OrderByDescending(h => h.CreatedAt).ThenByDescending(h => h.Id)
            .Select(h => new TaskHistoryDto(h.Id, h.Action, h.FromStatus, h.ToStatus, h.Details, h.ChangedBy != null ? h.ChangedBy.FullName : "System", h.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<TaskAttachmentDto> AddAttachmentAsync(int id, string fileName, string contentType, long size, Stream content,
        int? durationSeconds = null, CancellationToken ct = default)
    {
        await LoadVisibleAsync(id, ct);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AttachmentRules.IsAllowed(ext)) throw new BadRequestException($"File type '{ext}' is not allowed.");
        var kind = AttachmentRules.KindOf(ext, contentType);
        var max = AttachmentRules.MaxSize(kind);
        if (size <= 0 || size > max) throw new BadRequestException($"{kind} files must be between 1 byte and {max / (1024 * 1024)} MB.");

        // Check the real format of media files (they can be shown inline): read the first bytes, then rewind.
        if (!content.CanSeek)
        {
            var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            content = copy;
        }
        content.Position = 0;
        var head = new byte[16];
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        if (!AttachmentRules.SignatureMatches(ext, head[..read]))
            throw new BadRequestException($"The file content does not match its '{ext}' extension.");
        content.Position = 0;

        var stored = await storage.SaveAsync(content, ext, ct);
        var safeName = Path.GetFileName(fileName).Replace("\"", "");
        var att = new TaskAttachment
        {
            TaskId = id, UploadedByUserId = currentUser.UserId!.Value, FileName = safeName.Length > 255 ? safeName[^255..] : safeName,
            StoredFileName = stored, ContentType = AttachmentRules.SafeContentType(ext, kind), SizeBytes = size,
            DurationSeconds = durationSeconds is > 0 and < 24 * 3600 ? durationSeconds : null,
        };
        db.TaskAttachments.Add(att);
        db.TaskHistory.Add(new TaskHistory { TaskId = id, ChangedByUserId = currentUser.UserId, Action = "AttachmentAdded", Details = att.FileName });
        try { await db.SaveChangesAsync(ct); }
        catch { storage.Delete(stored); throw; }
        var name = await db.Users.Where(u => u.Id == att.UploadedByUserId).Select(u => u.FullName).FirstAsync(ct);
        return new TaskAttachmentDto(att.Id, att.FileName, att.ContentType, att.SizeBytes, att.UploadedByUserId, name, att.CreatedAt, kind, null,
            att.DurationSeconds);
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
        var open = query.Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled);
        var byPriority = await open.GroupBy(t => t.Priority).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var byType = await open.GroupBy(t => t.Type).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var blocked = await open.CountAsync(t => t.IsBlocked, ct);
        int C(TaskItemStatus s) => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0;
        return new TaskDashboardDto(counts.Sum(c => c.Count), C(TaskItemStatus.New), C(TaskItemStatus.Assigned), C(TaskItemStatus.InProgress),
            C(TaskItemStatus.PendingReview), C(TaskItemStatus.Completed), C(TaskItemStatus.Rejected), C(TaskItemStatus.Cancelled), overdue, dueWeek,
            Enum.GetValues<TaskPriority>().ToDictionary(p => p.ToString(), p => byPriority.FirstOrDefault(b => b.Key == p)?.Count ?? 0),
            blocked, Enum.GetValues<TaskType>().ToDictionary(t => t.ToString(), t => byType.FirstOrDefault(b => b.Key == t)?.Count ?? 0));
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
        var fromTasks = await (await VisibleTasksAsync(ct)).Where(t => t.ProjectId != null).Select(t => t.ProjectId!.Value).Distinct().ToArrayAsync(ct);
        var uid = currentUser.UserId ?? -1;
        var created = await db.Projects.Where(p => p.CreatedByUserId == uid).Select(p => p.Id).ToArrayAsync(ct);
        return fromTasks.Concat(created).Concat(await projectAccess.MemberProjectIdsAsync(ct)).Distinct().ToArray();
    }

    public async Task<(IReadOnlyList<TaskDto> Tasks, int[] OpenableIds)> GetProjectTasksAsync(int projectId, CancellationToken ct = default)
    {
        var tasks = await db.Tasks.AsNoTracking().Where(t => t.ProjectId == projectId).OrderBy(t => t.Id).Select(Projection(clock.UtcNow)).Take(5_000).ToListAsync(ct);
        var openable = await (await VisibleTasksAsync(ct)).Where(t => t.ProjectId == projectId).Select(t => t.Id).ToArrayAsync(ct);
        return (tasks, openable);
    }

    public async Task<IReadOnlyList<TaskDto>> GetSprintTasksAsync(int sprintId, CancellationToken ct = default) =>
        await (await VisibleTasksAsync(ct)).AsNoTracking().Where(t => t.SprintId == sprintId).OrderBy(t => t.Id)
            .Select(Projection(clock.UtcNow)).Take(5_000).ToListAsync(ct);

    // ------------------------------------------------------------------ helpers

    /// <summary>Saves; a task in a project without a number gets the next one. Retries if two tasks took the same number at once.</summary>
    private async Task SaveWithNumberAsync(TaskItem task, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (task.ProjectId is int pid && task.Number is null)
                task.Number = (await db.Tasks.Where(t => t.ProjectId == pid && t.Id != task.Id).MaxAsync(t => t.Number, ct) ?? 0) + 1;
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && task.ProjectId is not null && attempt < 3)
            {
                task.Number = null; // somebody else took that number: pick the next one
            }
        }
    }

    private static string NormalizeLabel(string label) => label.Trim().ToLowerInvariant();

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void ApplyPlanningFields(TaskItem task, string[]? labels, string? story, string? screen, string? design, string? pr)
    {
        task.Labels = (labels ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).Select(NormalizeLabel).Distinct().ToArray();
        task.StoryRef = Clean(story)?.ToUpperInvariant();
        task.ScreenRef = Clean(screen)?.ToUpperInvariant();
        task.DesignUrl = Clean(design);
        task.PullRequestUrl = Clean(pr);
    }

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

    /// <summary>One level of sub-tasks, inside the same project, and the parent must be visible.</summary>
    private async Task<int?> ValidateParentAsync(int? parentId, int? taskId, int? projectId, CancellationToken ct)
    {
        if (parentId is not int pid) return null;
        if (pid == taskId) throw new BadRequestException("A task cannot be its own parent.");
        var parent = await (await VisibleTasksAsync(ct)).AsNoTracking().Where(t => t.Id == pid)
                         .Select(t => new { t.ProjectId, t.ParentTaskId }).FirstOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Task", pid);
        if (parent.ParentTaskId is not null) throw new BadRequestException("Sub-tasks cannot have their own sub-tasks.");
        if (parent.ProjectId != projectId) throw new BadRequestException("A sub-task must be in the same project as its parent.");
        if (taskId is int id && await db.Tasks.AnyAsync(t => t.ParentTaskId == id, ct))
            throw new BadRequestException("A task that has sub-tasks cannot become a sub-task.");
        return pid;
    }

    /// <summary>Tasks can be planned into planned or active sprints; one already in a completed sprint may stay there.</summary>
    private async Task<int?> ValidateSprintAsync(int? sprintId, int? currentSprintId, CancellationToken ct)
    {
        if (sprintId is not int id) return null;
        var status = await db.Sprints.Where(s => s.Id == id).Select(s => (SprintStatus?)s.Status).FirstOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Sprint", id);
        if (id != currentSprintId && status == SprintStatus.Completed)
            throw new BadRequestException("Tasks cannot be added to a completed sprint.");
        return id;
    }

    private record AssigneeInfo(int Id, string Name, int DepartmentId, int? UserId);

    /// <param name="leadProjectId">A project the user leads: its members may be assigned too.</param>
    /// <param name="alreadyAssigned">People already on the task stay allowed (editing must not fail because of them).</param>
    private async Task<List<AssigneeInfo>> ValidateAssigneesAsync(int[] ids, int? leadProjectId, CancellationToken ct,
        IReadOnlySet<int>? alreadyAssigned = null)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0) return [];
        var found = await db.Employees.Where(e => distinct.Contains(e.Id) && !e.IsDeleted && e.IsActive)
            .Select(e => new AssigneeInfo(e.Id, e.FirstName + " " + e.LastName, e.DepartmentId, e.UserId)).ToListAsync(ct);
        if (found.Count != distinct.Length) throw new BadRequestException("One or more assignees do not exist or are inactive.");
        if (!currentUser.HasPermission(Permissions.TasksViewAll))
        {
            var scoped = await scope.TaskScopedEmployeesAsync(ct);
            var allowed = (await scoped.Where(e => distinct.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
            if (leadProjectId is int p)
                allowed.UnionWith(await db.ProjectMembers.Where(m => m.ProjectId == p && distinct.Contains(m.EmployeeId)).Select(m => m.EmployeeId).ToListAsync(ct));
            if (alreadyAssigned is not null) allowed.UnionWith(alreadyAssigned);
            if (distinct.Any(id => !allowed.Contains(id)))
                throw new ForbiddenException("You can only assign tasks to members of the departments you head, the teams you lead or the projects you lead.");
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

    public async Task<TaskTargetsDto> GetTargetsAsync(int? projectId = null, CancellationToken ct = default)
    {
        var leads = await projectAccess.LeadProjectIdsAsync(ct);
        if (!currentUser.HasPermission(Permissions.TasksAssign) && leads.Length == 0) throw new ForbiddenException("You cannot assign tasks.");
        var (deptIds, teamIds) = await AllowedTargetsAsync(ct);

        var departments = db.Departments.Where(d => d.IsActive);
        if (deptIds is not null) departments = departments.Where(d => deptIds.Contains(d.Id));
        var teams = db.Teams.Where(t => t.IsActive);
        if (teamIds is not null) teams = teams.Where(t => teamIds.Contains(t.Id));

        var employees = (await scope.TaskScopedEmployeesAsync(ct)).Where(e => e.IsActive);
        if (projectId is int p && leads.Contains(p) && !currentUser.HasPermission(Permissions.TasksViewAll))
        {
            var members = db.ProjectMembers.Where(m => m.ProjectId == p).Select(m => m.EmployeeId);
            var scopedIds = employees.Select(e => e.Id);
            employees = db.Employees.Where(e => !e.IsDeleted && e.IsActive && (scopedIds.Contains(e.Id) || members.Contains(e.Id)));
        }

        return new TaskTargetsDto(
            await departments.OrderBy(d => d.Name).Select(d => new TaskTargetDepartmentDto(d.Id, d.Name, d.NameAr, d.ManagerEmployeeId)).ToListAsync(ct),
            await teams.OrderBy(t => t.Name).Select(t => new TaskTargetTeamDto(t.Id, t.Name, t.NameAr, t.DepartmentId, t.LeaderEmployeeId)).ToListAsync(ct),
            await employees.OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
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
