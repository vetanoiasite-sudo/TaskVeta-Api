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

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public TaskItemStatus Status { get; set; } = TaskItemStatus.New;
    public int ProgressPercent { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? CompletionDate { get; set; }
    public string? ReviewNotes { get; set; }

    public DateTime? DeadlineReminderSentAt { get; set; }
    public DateTime? OverdueNotifiedAt { get; set; }

    /// <summary>Optimistic-concurrency token (PostgreSQL system column <c>xmin</c>).</summary>
    public uint RowVersion { get; set; }

    public ICollection<TaskAssignment> Assignments { get; set; } = new List<TaskAssignment>();
    public ICollection<TaskComment> Comments { get; set; } = new List<TaskComment>();
    public ICollection<TaskAttachment> Attachments { get; set; } = new List<TaskAttachment>();
    public ICollection<TaskHistory> History { get; set; } = new List<TaskHistory>();

    public bool IsClosed => Status is TaskItemStatus.Completed or TaskItemStatus.Cancelled;
    public bool IsOverdue(DateTime utcNow) => !IsClosed && DueDate.HasValue && DueDate.Value < utcNow;
}

public class TaskAssignment : BaseEntity
{
    public int TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public int AssignedByUserId { get; set; }
}

public class TaskComment : BaseEntity
{
    public int TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
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
}

public class TaskHistory : BaseEntity
{
    public int TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public int? ChangedByUserId { get; set; }
    public ApplicationUser? ChangedBy { get; set; }
    public string Action { get; set; } = string.Empty;
    public TaskItemStatus? FromStatus { get; set; }
    public TaskItemStatus? ToStatus { get; set; }
    public string? Details { get; set; }
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
