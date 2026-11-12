namespace CompanyManagement.Domain.Enums;

public enum AttendanceStatus
{
    Present = 1,
    Absent = 2,
    Late = 3,
    HalfDay = 4,
    EarlyLeave = 5,
    OnLeave = 6,
    Holiday = 7,
    WorkFromHome = 8
}

public enum AttendanceSource
{
    Web = 1,
    QrCode = 2,
    Manual = 3,
    System = 4
}

[Flags]
public enum VerificationMethod
{
    None = 0,
    Geofence = 1,
    Network = 2,
    QrCode = 4
}

public enum VerificationPolicy
{
    /// <summary>At least one of the enabled methods must succeed.</summary>
    Any = 1,
    /// <summary>Every enabled method must succeed.</summary>
    All = 2
}

public enum TaskPriority
{
    Low = 1,
    Medium = 2,
    High = 3,
    Urgent = 4
}

public enum ProjectStatus
{
    Planned = 1,
    Active = 2,
    OnHold = 3,
    Completed = 4,
    Cancelled = 5
}

public enum TaskItemStatus
{
    New = 1,
    Assigned = 2,
    InProgress = 3,
    PendingReview = 4,
    Completed = 5,
    Rejected = 6,
    Cancelled = 7
}

public enum LeaveType
{
    Annual = 1,
    Sick = 2,
    Unpaid = 3,
    Emergency = 4,
    Other = 5
}

public enum LeaveStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Cancelled = 4
}

public enum NotificationType
{
    TaskAssigned = 1,
    TaskDeadlineApproaching = 2,
    TaskOverdue = 3,
    TaskApproved = 4,
    TaskRejected = 5,
    TaskComment = 6,
    TaskStatusChanged = 7,
    AttendanceUpdated = 8,
    AttendanceAttention = 9,
    LeaveRequest = 10,
    System = 11,
    TaskMention = 12
}
