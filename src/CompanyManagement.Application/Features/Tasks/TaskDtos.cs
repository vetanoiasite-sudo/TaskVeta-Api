using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Enums;
using FluentValidation;

namespace CompanyManagement.Application.Features.Tasks;

public record TaskAssigneeDto(int EmployeeId, string EmployeeCode, string FullName);

public record TaskDto(int Id, string Title, TaskPriority Priority, TaskItemStatus Status, int ProgressPercent, DateTime? StartDate,
    DateTime? DueDate, DateTime? CompletionDate, bool IsOverdue, int AssignedByUserId, string AssignedByName, int? DepartmentId,
    string? DepartmentName, int? TeamId, string? TeamName, int? ProjectId, string? ProjectName, IReadOnlyList<TaskAssigneeDto> Assignees, int CommentCount, int AttachmentCount, DateTime CreatedAt,
    DateTime UpdatedAt, int? Rating);

public record TaskCommentDto(int Id, int UserId, string UserName, string Content, DateTime CreatedAt, int[] MentionedUserIds);

/// <summary>Someone involved in a task (can be @mentioned) with read receipts. Role: Assignee, Creator or Leader.</summary>
public record TaskParticipantDto(int UserId, string Name, int? EmployeeId, string Role, DateTime? FirstSeenAt, DateTime? LastSeenAt);

public record TaskAttachmentDto(int Id, string FileName, string ContentType, long SizeBytes, int UploadedByUserId, string UploadedByName,
    DateTime CreatedAt);

public record TaskHistoryDto(int Id, string Action, TaskItemStatus? FromStatus, TaskItemStatus? ToStatus, string? Details,
    string? ChangedBy, DateTime CreatedAt);

public record TaskReviewDto(int Id, bool Approved, int? Rating, string? Notes, int ReviewerUserId, string ReviewerName, DateTime CreatedAt);

public record TaskPermissionsDto(bool CanEdit, bool CanReview, bool CanUpdateProgress, bool CanComment, TaskItemStatus[] AllowedStatuses);

public record TaskDetailsDto(TaskDto Task, string? Description, string? ReviewNotes, IReadOnlyList<TaskCommentDto> Comments,
    IReadOnlyList<TaskAttachmentDto> Attachments, TaskPermissionsDto Permissions, string RowVersion, IReadOnlyList<TaskParticipantDto> Participants,
    IReadOnlyList<TaskReviewDto> Reviews);

public class TaskQuery : PagedRequest
{
    public TaskItemStatus? Status { get; set; }
    public TaskPriority? Priority { get; set; }
    public int? AssigneeId { get; set; }
    public int? DepartmentId { get; set; }
    public int? TeamId { get; set; }
    public int? ProjectId { get; set; }
    public bool? Overdue { get; set; }
    public DateTime? DueFrom { get; set; }
    public DateTime? DueTo { get; set; }
    /// <summary>Only tasks assigned to the current user.</summary>
    public bool Mine { get; set; }
    /// <summary>Only tasks created by the current user.</summary>
    public bool CreatedByMe { get; set; }
}

public record CreateTaskRequest(string Title, string? Description, TaskPriority Priority, DateTime? StartDate, DateTime? DueDate,
    int? DepartmentId, int[] AssigneeEmployeeIds, int? TeamId = null, int? ProjectId = null);

public record UpdateTaskRequest(string Title, string? Description, TaskPriority Priority, DateTime? StartDate, DateTime? DueDate,
    int? DepartmentId, int[] AssigneeEmployeeIds, string? RowVersion, int? TeamId = null, int? ProjectId = null);

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
public record AddCommentRequest(string Content, int[]? MentionedUserIds = null);

public record TaskDashboardDto(int Total, int New, int Assigned, int InProgress, int PendingReview, int Completed, int Rejected,
    int Cancelled, int Overdue, int DueThisWeek, IReadOnlyDictionary<string, int> ByPriority);

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
        RuleFor(x => x.Content).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.MentionedUserIds).Must(m => m is null || m.Length <= 50).WithMessage("Too many mentions.");
    }
}
