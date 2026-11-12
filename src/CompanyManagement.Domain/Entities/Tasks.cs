using CompanyManagement.Domain.Common;
using CompanyManagement.Domain.Enums;

namespace CompanyManagement.Domain.Entities;

public class TaskItem : BaseEntity
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public int AssignedByUserId { get; set; }
    public ApplicationUser AssignedBy { get; set; } = null!;

    /// <summary>Primary assignee. All assignees (including this one) are listed in <see cref="Assignments"/>.</summary>
    public int? AssignedToEmployeeId { get; set; }
    public Employee? AssignedTo { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>Optional team the task was given to (its members are the expected assignees).</summary>
    public int? TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>Optional project the task belongs to.</summary>
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>Sequence number inside the project; with the project code it forms the task key (e.g. GA-123). Null without a project.</summary>
    public int? Number { get; set; }

    public TaskType Type { get; set; } = TaskType.Feature;

    /// <summary>Sprint the task is planned in; null = backlog.</summary>
    public int? SprintId { get; set; }
    public Sprint? Sprint { get; set; }

    /// <summary>Parent task when this is a sub-task (one level only).</summary>
    public int? ParentTaskId { get; set; }
    public TaskItem? ParentTask { get; set; }
    public ICollection<TaskItem> Subtasks { get; set; } = new List<TaskItem>();

    /// <summary>Estimate in working days (0.5 steps). Tasks above 3 days should be split.</summary>
    public decimal? EstimateDays { get; set; }
    public string[] Labels { get; set; } = [];

    /// <summary>User story id from the project's Business Requirements (e.g. US-CRM-12).</summary>
    public string? StoryRef { get; set; }
    /// <summary>Screen id from the UI/UX screen inventory (e.g. SCR-CRM-03).</summary>
    public string? ScreenRef { get; set; }
    public string? DesignUrl { get; set; }
    public string? PullRequestUrl { get; set; }

    public bool IsBlocked { get; set; }
    public string? BlockedReason { get; set; }
    public DateTime? BlockedAt { get; set; }

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public TaskItemStatus Status { get; set; } = TaskItemStatus.New;
    public int ProgressPercent { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? CompletionDate { get; set; }
    public string? ReviewNotes { get; set; }
    /// <summary>Rating (1–10) given by the reviewer when the task was approved.</summary>
    public int? Rating { get; set; }

    public DateTime? DeadlineReminderSentAt { get; set; }
    public DateTime? OverdueNotifiedAt { get; set; }

    /// <summary>Optimistic-concurrency token (PostgreSQL system column <c>xmin</c>).</summary>
    public uint RowVersion { get; set; }

    public ICollection<TaskAssignment> Assignments { get; set; } = new List<TaskAssignment>();
    public ICollection<TaskComment> Comments { get; set; } = new List<TaskComment>();
    public ICollection<TaskAttachment> Attachments { get; set; } = new List<TaskAttachment>();
    public ICollection<TaskHistory> History { get; set; } = new List<TaskHistory>();
    public ICollection<TaskReview> Reviews { get; set; } = new List<TaskReview>();
    /// <summary>Tasks that must be finished before this one (this task is blocked by them).</summary>
    public ICollection<TaskDependency> BlockedBy { get; set; } = new List<TaskDependency>();
    /// <summary>Tasks waiting for this one.</summary>
    public ICollection<TaskDependency> Blocks { get; set; } = new List<TaskDependency>();

    public bool IsClosed => Status is TaskItemStatus.Completed or TaskItemStatus.Cancelled;
    public bool IsOverdue(DateTime utcNow) => !IsClosed && DueDate.HasValue && DueDate.Value < utcNow;
}

/// <summary><see cref="TaskId"/> cannot really finish before <see cref="BlockedByTaskId"/> (e.g. frontend waiting for an API).</summary>
public class TaskDependency : BaseEntity
{
    public int TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int BlockedByTaskId { get; set; }
    public TaskItem BlockedByTask { get; set; } = null!;
}

public class TaskAssignment : BaseEntity
{
    public int TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public int AssignedByUserId { get; set; }
}

public class TaskComment : BaseEntity, ISourceTracked
{
    /// <summary>"Claude" when written through the MCP endpoint.</summary>
    public string? Source { get; set; }
    public int TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    /// <summary>Users @mentioned in the comment (they get a mention notification).</summary>
    public int[] MentionedUserIds { get; set; } = [];
    /// <summary>Files sent with the comment: voice notes, pasted screenshots, recordings.</summary>
    public ICollection<TaskAttachment> Attachments { get; set; } = new List<TaskAttachment>();
    public ICollection<TaskCommentReaction> Reactions { get; set; } = new List<TaskCommentReaction>();
}

/// <summary>An emoji reaction on a comment (👍 ❤️ 😂 …). One row per user and emoji.</summary>
public class TaskCommentReaction : BaseEntity
{
    public const int MaxEmojiLength = 16;

    public int CommentId { get; set; }
    public TaskComment Comment { get; set; } = null!;
    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public string Emoji { get; set; } = string.Empty;
}

/// <summary>A reviewer's decision on a submitted task: approved (rating required) or rejected (notes required).</summary>
public class TaskReview : BaseEntity
{
    public const int MinRating = 1;
    public const int MaxRating = 10;

    public int TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int ReviewerUserId { get; set; }
    public ApplicationUser Reviewer { get; set; } = null!;
    public bool Approved { get; set; }
    public int? Rating { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Read receipt: when a user first and last opened a task.</summary>
public class TaskView : BaseEntity
{
    public int TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

public class TaskAttachment : BaseEntity
{
    public int TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int UploadedByUserId { get; set; }
    public ApplicationUser UploadedBy { get; set; } = null!;
    public string FileName { get; set; } = string.Empty;
    /// <summary>Random server-side name; files are stored outside the web root.</summary>
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    /// <summary>The comment this file was sent with (voice note, pasted screenshot); null for files added to the task directly.</summary>
    public int? CommentId { get; set; }
    public TaskComment? Comment { get; set; }
    /// <summary>Length of a voice note / video in seconds, as reported by the recorder.</summary>
    public int? DurationSeconds { get; set; }
}

public class TaskHistory : BaseEntity, ISourceTracked
{
    public int TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int? ChangedByUserId { get; set; }
    public ApplicationUser? ChangedBy { get; set; }
    public string Action { get; set; } = string.Empty;
    public TaskItemStatus? FromStatus { get; set; }
    public TaskItemStatus? ToStatus { get; set; }
    public string? Details { get; set; }
    /// <summary>Where the change came from: null for the TaskVeta app, "Claude" when made through the MCP endpoint.</summary>
    public string? Source { get; set; }
}

/// <summary>Allowed task status transitions.</summary>
public static class TaskWorkflow
{
    private static readonly Dictionary<TaskItemStatus, TaskItemStatus[]> AssigneeTransitions = new()
    {
        [TaskItemStatus.New] = [TaskItemStatus.InProgress],
        [TaskItemStatus.Assigned] = [TaskItemStatus.InProgress],
        [TaskItemStatus.InProgress] = [TaskItemStatus.PendingReview],
        [TaskItemStatus.Rejected] = [TaskItemStatus.InProgress, TaskItemStatus.PendingReview],
    };

    private static readonly Dictionary<TaskItemStatus, TaskItemStatus[]> ReviewerTransitions = new()
    {
        [TaskItemStatus.New] = [TaskItemStatus.Assigned, TaskItemStatus.InProgress, TaskItemStatus.Cancelled],
        [TaskItemStatus.Assigned] = [TaskItemStatus.InProgress, TaskItemStatus.Cancelled],
        [TaskItemStatus.InProgress] = [TaskItemStatus.PendingReview, TaskItemStatus.Cancelled, TaskItemStatus.Assigned],
        [TaskItemStatus.PendingReview] = [TaskItemStatus.Completed, TaskItemStatus.Rejected, TaskItemStatus.InProgress, TaskItemStatus.Cancelled],
        [TaskItemStatus.Rejected] = [TaskItemStatus.InProgress, TaskItemStatus.PendingReview, TaskItemStatus.Cancelled, TaskItemStatus.Assigned],
        [TaskItemStatus.Completed] = [TaskItemStatus.InProgress],
        [TaskItemStatus.Cancelled] = [TaskItemStatus.Assigned],
    };

    public static bool CanTransition(TaskItemStatus from, TaskItemStatus to, bool isReviewer, bool isAssignee)
    {
        if (from == to) return false;
        if (isReviewer && ReviewerTransitions.TryGetValue(from, out var r) && r.Contains(to)) return true;
        if (isAssignee && AssigneeTransitions.TryGetValue(from, out var a) && a.Contains(to)) return true;
        return false;
    }
}
