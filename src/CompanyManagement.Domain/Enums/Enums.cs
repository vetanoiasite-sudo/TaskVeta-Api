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

/// <summary>Kind of work (Way of Working §3.1). Drives the sprint capacity split: features / improvements + tech-debt / bugs.</summary>
public enum TaskType
{
    Feature = 1,
    Improvement = 2,
    Bug = 3,
    TechDebt = 4,
    Design = 5,
    Docs = 6
}

/// <summary>A person's role inside one project. A person may hold several roles in the same project.</summary>
public enum ProjectRole
{
    BrOwner = 1,
    UiUxOwner = 2,
    FrontendLead = 3,
    BackendLead = 4,
    MobileLead = 5,
    QaOwner = 6,
    Member = 7
}

public enum SprintStatus
{
    Planned = 1,
    Active = 2,
    Completed = 3
}

/// <summary>Shown in the UI as P3 (Low) … P0 (Urgent).</summary>
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
    TaskMention = 12,
    TaskBlocked = 13,
    ProjectMembership = 14
}

/// <summary>Follows the company documentation templates (01-DISCOVERY … 07-SUPPORT).</summary>
public enum ProjectDocumentCategory
{
    Discovery = 1,
    BusinessRequirements = 2,
    /// <summary>The UI/UX documentation (03-UIUX-DESIGN): screens with SCR codes, flows, states. The design itself stays in Figma.</summary>
    Design = 3,
    Technical = 4,
    Testing = 5,
    Meeting = 6,
    Client = 7,
    Other = 8
}
