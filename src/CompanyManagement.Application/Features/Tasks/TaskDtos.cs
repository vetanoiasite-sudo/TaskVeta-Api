using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Enums;
using FluentValidation;

namespace CompanyManagement.Application.Features.Tasks;

public record TaskAssigneeDto(int EmployeeId, string EmployeeCode, string FullName);

public record TaskDto(int Id, string Title, TaskPriority Priority, TaskItemStatus Status, int ProgressPercent, DateTime? StartDate,
    DateTime? DueDate, DateTime? CompletionDate, bool IsOverdue, int AssignedByUserId, string AssignedByName, int? DepartmentId,
    string? DepartmentName, IReadOnlyList<TaskAssigneeDto> Assignees, int CommentCount, int AttachmentCount, DateTime CreatedAt,
    DateTime UpdatedAt);

public record TaskCommentDto(int Id, int UserId, string UserName, string Content, DateTime CreatedAt);

public record TaskAttachmentDto(int Id, string FileName, string ContentType, long SizeBytes, int UploadedByUserId, string UploadedByName,
    DateTime CreatedAt);

public record TaskHistoryDto(int Id, string Action, TaskItemStatus? FromStatus, TaskItemStatus? ToStatus, string? Details,
    string? ChangedBy, DateTime CreatedAt);

public record TaskPermissionsDto(bool CanEdit, bool CanReview, bool CanUpdateProgress, bool CanComment, TaskItemStatus[] AllowedStatuses);

public record TaskDetailsDto(TaskDto Task, string? Description, string? ReviewNotes, IReadOnlyList<TaskCommentDto> Comments,
    IReadOnlyList<TaskAttachmentDto> Attachments, TaskPermissionsDto Permissions, string RowVersion);

public class TaskQuery : PagedRequest
{
    public TaskItemStatus? Status { get; set; }
    public TaskPriority? Priority { get; set; }
    public int? AssigneeId { get; set; }
    public int? DepartmentId { get; set; }
    public bool? Overdue { get; set; }
    public DateTime? DueFrom { get; set; }
    public DateTime? DueTo { get; set; }
    /// <summary>Only tasks assigned to the current user.</summary>
    public bool Mine { get; set; }
    /// <summary>Only tasks created by the current user.</summary>
    public bool CreatedByMe { get; set; }
}

public record CreateTaskRequest(string Title, string? Description, TaskPriority Priority, DateTime? StartDate, DateTime? DueDate,
    int? DepartmentId, int[] AssigneeEmployeeIds);

public record UpdateTaskRequest(string Title, string? Description, TaskPriority Priority, DateTime? StartDate, DateTime? DueDate,
    int? DepartmentId, int[] AssigneeEmployeeIds, string? RowVersion);

public record UpdateTaskStatusRequest(TaskItemStatus Status, string? Note, int? ProgressPercent);
public record UpdateTaskProgressRequest(int ProgressPercent, string? Note);
public record AddCommentRequest(string Content);

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
    public AddCommentRequestValidator() => RuleFor(x => x.Content).NotEmpty().MaximumLength(4000);
}
