using System.Text.RegularExpressions;
using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Enums;
using FluentValidation;

namespace CompanyManagement.Application.Features.Tasks;

public record TaskAssigneeDto(int EmployeeId, string EmployeeCode, string FullName);

/// <param name="Key">Human task id: project code + number (GA-123), or #id for tasks without a project.</param>
public record TaskDto(int Id, string Key, string Title, TaskType Type, TaskPriority Priority, TaskItemStatus Status, int ProgressPercent,
    DateTime? StartDate, DateTime? DueDate, DateTime? CompletionDate, bool IsOverdue, int AssignedByUserId, string AssignedByName,
    int? DepartmentId, string? DepartmentName, int? TeamId, string? TeamName, int? ProjectId, string? ProjectName, string? ProjectCode,
    int? SprintId, string? SprintName, int? ParentTaskId, decimal? EstimateDays, string[] Labels, string? StoryRef, string? ScreenRef,
    bool IsBlocked, string? BlockedReason, int SubtaskCount, int SubtaskDoneCount, int OpenDependencyCount,
    IReadOnlyList<TaskAssigneeDto> Assignees, int CommentCount, int AttachmentCount, DateTime CreatedAt, DateTime UpdatedAt, int? Rating);

/// <summary>A short reference to another task (parent, sub-task, dependency).</summary>
public record TaskRefDto(int Id, string Key, string Title, TaskType Type, TaskItemStatus Status);

public record TaskCommentDto(int Id, int UserId, string UserName, string Content, DateTime CreatedAt, int[] MentionedUserIds,
    IReadOnlyList<TaskAttachmentDto> Attachments, IReadOnlyList<TaskReactionDto> Reactions);

public record TaskReactionUserDto(int UserId, string Name);
/// <summary>One emoji on a comment with who used it.</summary>
public record TaskReactionDto(string Emoji, int Count, IReadOnlyList<TaskReactionUserDto> Users);

/// <summary>Adds the emoji to the comment, or removes it if you already reacted with it.</summary>
public record ToggleReactionRequest(string Emoji);

/// <summary>Someone involved in a task (can be @mentioned) with read receipts. Role: Assignee, Creator, Leader or ProjectLead.</summary>
public record TaskParticipantDto(int UserId, string Name, int? EmployeeId, string Role, DateTime? FirstSeenAt, DateTime? LastSeenAt);

/// <param name="Kind">Document, Image, Audio or Video. Media can be previewed inline (GET …/attachments/{id}?inline=true).</param>
/// <param name="CommentId">Set when the file was sent with a comment (voice note, pasted screenshot).</param>
public record TaskAttachmentDto(int Id, string FileName, string ContentType, long SizeBytes, int UploadedByUserId, string UploadedByName,
    DateTime CreatedAt, AttachmentKind Kind, int? CommentId, int? DurationSeconds);

public record TaskHistoryDto(int Id, string Action, TaskItemStatus? FromStatus, TaskItemStatus? ToStatus, string? Details,
    string? ChangedBy, DateTime CreatedAt);

public record TaskReviewDto(int Id, bool Approved, int? Rating, string? Notes, int ReviewerUserId, string ReviewerName, DateTime CreatedAt);

/// <param name="CanBlock">Assignees and reviewers can flag / clear Blocked.</param>
/// <param name="CanPlan">Can move the task to a sprint or back to the backlog.</param>
public record TaskPermissionsDto(bool CanEdit, bool CanReview, bool CanUpdateProgress, bool CanComment, TaskItemStatus[] AllowedStatuses,
    bool CanBlock, bool CanPlan);

public record TaskDetailsDto(TaskDto Task, string? Description, string? ReviewNotes, string? DesignUrl, string? PullRequestUrl,
    TaskRefDto? Parent, IReadOnlyList<TaskRefDto> Subtasks, IReadOnlyList<TaskRefDto> BlockedBy, IReadOnlyList<TaskRefDto> Blocks,
    IReadOnlyList<TaskCommentDto> Comments, IReadOnlyList<TaskAttachmentDto> Attachments, TaskPermissionsDto Permissions, string RowVersion,
    IReadOnlyList<TaskParticipantDto> Participants, IReadOnlyList<TaskReviewDto> Reviews);

public class TaskQuery : PagedRequest
{
    public TaskItemStatus? Status { get; set; }
    public TaskPriority? Priority { get; set; }
    public TaskType? Type { get; set; }
    public int? AssigneeId { get; set; }
    public int? DepartmentId { get; set; }
    public int? TeamId { get; set; }
    public int? ProjectId { get; set; }
    public int? SprintId { get; set; }
    /// <summary>Only open tasks not planned in any sprint.</summary>
    public bool Backlog { get; set; }
    public int? ParentTaskId { get; set; }
    /// <summary>Hide sub-tasks (show only top-level tasks).</summary>
    public bool TopLevelOnly { get; set; }
    public string? Label { get; set; }
    public bool? Blocked { get; set; }
    public bool? Overdue { get; set; }
    public DateTime? DueFrom { get; set; }
    public DateTime? DueTo { get; set; }
    /// <summary>Only tasks assigned to the current user.</summary>
    public bool Mine { get; set; }
    /// <summary>Only tasks created by the current user.</summary>
    public bool CreatedByMe { get; set; }
}

public record CreateTaskRequest(string Title, string? Description, TaskPriority Priority, DateTime? StartDate, DateTime? DueDate,
    int? DepartmentId, int[] AssigneeEmployeeIds, int? TeamId = null, int? ProjectId = null, TaskType Type = TaskType.Feature,
    int? SprintId = null, int? ParentTaskId = null, decimal? EstimateDays = null, string[]? Labels = null, string? StoryRef = null,
    string? ScreenRef = null, string? DesignUrl = null, string? PullRequestUrl = null);

public record UpdateTaskRequest(string Title, string? Description, TaskPriority Priority, DateTime? StartDate, DateTime? DueDate,
    int? DepartmentId, int[] AssigneeEmployeeIds, string? RowVersion, int? TeamId = null, int? ProjectId = null,
    TaskType Type = TaskType.Feature, int? SprintId = null, int? ParentTaskId = null, decimal? EstimateDays = null, string[]? Labels = null,
    string? StoryRef = null, string? ScreenRef = null, string? DesignUrl = null, string? PullRequestUrl = null);

/// <summary>What the current user may pick in the task form: departments, teams and assignable employees.</summary>
public record TaskTargetsDto(IReadOnlyList<TaskTargetDepartmentDto> Departments, IReadOnlyList<TaskTargetTeamDto> Teams,
    IReadOnlyList<TaskTargetEmployeeDto> Employees);
public record TaskTargetDepartmentDto(int Id, string Name, string? NameAr, int? ManagerEmployeeId);
public record TaskTargetTeamDto(int Id, string Name, string? NameAr, int DepartmentId, int? LeaderEmployeeId);
public record TaskTargetEmployeeDto(int Id, string Name, string Code, int DepartmentId, int[] TeamIds);

/// <summary>Task figures for one project (over the tasks visible to the current user).</summary>
public record ProjectTaskStats(int Total, int Completed, int Overdue, int ProgressPercent);

/// <summary>Rating (1–10) is required when approving (Completed) and optional when rejecting.</summary>
public record UpdateTaskStatusRequest(TaskItemStatus Status, string? Note, int? ProgressPercent, int? Rating = null);
public record UpdateTaskProgressRequest(int ProgressPercent, string? Note);
/// <param name="AttachmentIds">Files you uploaded to this task (not yet sent with a comment) to attach — e.g. a voice note. Text is optional then.</param>
public record AddCommentRequest(string? Content, int[]? MentionedUserIds = null, int[]? AttachmentIds = null);
/// <summary>Flag or clear Blocked. A reason is required when blocking.</summary>
public record SetTaskBlockedRequest(bool IsBlocked, string? Reason);
/// <summary>Move a task to a sprint (or to the backlog with null).</summary>
public record SetTaskSprintRequest(int? SprintId);
public record AddDependencyRequest(int BlockedByTaskId);

public record TaskDashboardDto(int Total, int New, int Assigned, int InProgress, int PendingReview, int Completed, int Rejected,
    int Cancelled, int Overdue, int DueThisWeek, IReadOnlyDictionary<string, int> ByPriority, int Blocked,
    IReadOnlyDictionary<string, int> ByType);

internal static partial class TaskRules
{
    public const int MaxLabels = 10;
    public const int MaxLabelLength = 30;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _.-]*$")]
    public static partial Regex LabelPattern();

    /// <summary>Document ids like US-CRM-12 or SCR-CRM-03-W.</summary>
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*(-[A-Za-z0-9]+)+$")]
    public static partial Regex RefPattern();

    public static bool IsHttpUrl(string? url) =>
        url is null || (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp));

    /// <summary>0.5-day steps, 0.5–30.</summary>
    public static bool IsValidEstimate(decimal? d) => d is null || (d > 0 && d <= 30 && d % 0.5m == 0);

    public static void AddPlanningRules<T>(AbstractValidator<T> v, Func<T, TaskType> type, Func<T, decimal?> estimate,
        Func<T, string[]?> labels, Func<T, string?> story, Func<T, string?> screen, Func<T, string?> design, Func<T, string?> pr)
    {
        v.RuleFor(x => type(x)).IsInEnum().OverridePropertyName("Type");
        v.RuleFor(x => estimate(x)).Must(IsValidEstimate).OverridePropertyName("EstimateDays")
            .WithMessage("Estimate must be between 0.5 and 30 days, in half-day steps.");
        v.RuleFor(x => labels(x)).Must(l => l is null || l.Length <= MaxLabels).OverridePropertyName("Labels")
            .WithMessage($"At most {MaxLabels} labels.");
        v.RuleForEach(x => labels(x) ?? Array.Empty<string>()).Must(l => !string.IsNullOrWhiteSpace(l) && l.Trim().Length <= MaxLabelLength && LabelPattern().IsMatch(l.Trim()))
            .OverridePropertyName("Labels").WithMessage($"Labels use letters, numbers, spaces, '-', '_' or '.', up to {MaxLabelLength} characters.");
        v.RuleFor(x => story(x)).Must(s => string.IsNullOrWhiteSpace(s) || (s.Trim().Length <= 50 && RefPattern().IsMatch(s.Trim())))
            .OverridePropertyName("StoryRef").WithMessage("Story reference looks like US-CRM-12.");
        v.RuleFor(x => screen(x)).Must(s => string.IsNullOrWhiteSpace(s) || (s.Trim().Length <= 50 && RefPattern().IsMatch(s.Trim())))
            .OverridePropertyName("ScreenRef").WithMessage("Screen reference looks like SCR-CRM-03.");
        v.RuleFor(x => design(x)).Must(u => string.IsNullOrWhiteSpace(u) || (u.Length <= 500 && IsHttpUrl(u.Trim())))
            .OverridePropertyName("DesignUrl").WithMessage("Design link must be a valid http(s) URL.");
        v.RuleFor(x => pr(x)).Must(u => string.IsNullOrWhiteSpace(u) || (u.Length <= 500 && IsHttpUrl(u.Trim())))
            .OverridePropertyName("PullRequestUrl").WithMessage("Pull request link must be a valid http(s) URL.");
    }
}

public class CreateTaskRequestValidator : AbstractValidator<CreateTaskRequest>
{
    public CreateTaskRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(4000);
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.DueDate).GreaterThanOrEqualTo(x => x.StartDate).When(x => x.StartDate.HasValue && x.DueDate.HasValue)
            .WithMessage("Due date must be after the start date.");
        RuleFor(x => x.AssigneeEmployeeIds).NotNull().Must(a => a.Length <= 100).WithMessage("Too many assignees.");
        TaskRules.AddPlanningRules(this, x => x.Type, x => x.EstimateDays, x => x.Labels, x => x.StoryRef, x => x.ScreenRef,
            x => x.DesignUrl, x => x.PullRequestUrl);
    }
}

public class UpdateTaskRequestValidator : AbstractValidator<UpdateTaskRequest>
{
    public UpdateTaskRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(4000);
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.DueDate).GreaterThanOrEqualTo(x => x.StartDate).When(x => x.StartDate.HasValue && x.DueDate.HasValue)
            .WithMessage("Due date must be after the start date.");
        RuleFor(x => x.AssigneeEmployeeIds).NotNull().Must(a => a.Length <= 100).WithMessage("Too many assignees.");
        TaskRules.AddPlanningRules(this, x => x.Type, x => x.EstimateDays, x => x.Labels, x => x.StoryRef, x => x.ScreenRef,
            x => x.DesignUrl, x => x.PullRequestUrl);
    }
}

public class UpdateTaskStatusRequestValidator : AbstractValidator<UpdateTaskStatusRequest>
{
    public UpdateTaskStatusRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Note).MaximumLength(2000);
        RuleFor(x => x.Note).NotEmpty().When(x => x.Status == TaskItemStatus.Rejected).WithMessage("A reason is required when rejecting a task.");
        RuleFor(x => x.ProgressPercent).InclusiveBetween(0, 100).When(x => x.ProgressPercent.HasValue);
        RuleFor(x => x.Rating).NotNull().When(x => x.Status == TaskItemStatus.Completed)
            .WithMessage("A rating from 1 to 10 is required when approving a task.");
        RuleFor(x => x.Rating).InclusiveBetween(1, 10).When(x => x.Rating.HasValue);
    }
}

public class UpdateTaskProgressRequestValidator : AbstractValidator<UpdateTaskProgressRequest>
{
    public UpdateTaskProgressRequestValidator()
    {
        RuleFor(x => x.ProgressPercent).InclusiveBetween(0, 100);
        RuleFor(x => x.Note).MaximumLength(2000);
    }
}

public class AddCommentRequestValidator : AbstractValidator<AddCommentRequest>
{
    public AddCommentRequestValidator()
    {
        RuleFor(x => x.Content).NotEmpty().When(x => x.AttachmentIds is not { Length: > 0 }).WithMessage("Write a comment or attach a file.");
        RuleFor(x => x.Content).MaximumLength(4000);
        RuleFor(x => x.MentionedUserIds).Must(m => m is null || m.Length <= 50).WithMessage("Too many mentions.");
        RuleFor(x => x.AttachmentIds).Must(a => a is null || a.Length <= 10).WithMessage("At most 10 files per comment.");
    }
}

public class ToggleReactionRequestValidator : AbstractValidator<ToggleReactionRequest>
{
    public ToggleReactionRequestValidator()
    {
        // An emoji (possibly with skin tone / variation selectors / ZWJ): no letters, digits, spaces or markup.
        RuleFor(x => x.Emoji).NotEmpty().MaximumLength(Domain.Entities.TaskCommentReaction.MaxEmojiLength)
            .Must(e => e is not null && e.All(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c != '<' && c != '>' && c != '&')
                       && e.Any(c => char.IsSurrogate(c) || c > 0x2000))
            .WithMessage("Pick an emoji.");
    }
}

public class SetTaskBlockedRequestValidator : AbstractValidator<SetTaskBlockedRequest>
{
    public SetTaskBlockedRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().When(x => x.IsBlocked).WithMessage("Say what the task is waiting for.");
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}
