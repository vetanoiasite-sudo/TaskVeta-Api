using CompanyManagement.Domain.Common;
using CompanyManagement.Domain.Enums;

namespace CompanyManagement.Domain.Entities;

/// <summary>
/// A company project that tasks can belong to. Its <see cref="Code"/> prefixes task keys (e.g. GA-123).
/// Managed by admins, its creator and its BR Owner; its timeline is built from its tasks.
/// </summary>
public class Project : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Planned;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public string? ClientName { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? DesignUrl { get; set; }
    public string? DocsUrl { get; set; }

    /// <summary>Who created the project (manages it like its BR Owner). Null for projects created before this field existed.</summary>
    public int? CreatedByUserId { get; set; }
    public ApplicationUser? CreatedBy { get; set; }

    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
    public ICollection<ProjectMember> Members { get; set; } = new List<ProjectMember>();
    public ICollection<ProjectDocument> Documents { get; set; } = new List<ProjectDocument>();
}

/// <summary>A person's role in a project. One row per (project, employee, role).</summary>
public class ProjectMember : BaseEntity
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public ProjectRole Role { get; set; } = ProjectRole.Member;

    /// <summary>Lead roles (everything except <see cref="ProjectRole.Member"/>) can create and review the project's tasks.</summary>
    public static bool IsLeadRole(ProjectRole role) => role != ProjectRole.Member;
}

/// <summary>A company-wide iteration (normally two weeks). Tasks from any project can be planned into it.</summary>
public class Sprint : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Goal { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public SprintStatus Status { get; set; } = SprintStatus.Planned;
    public DateTime? CompletedAt { get; set; }

    public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
}

/// <summary>
/// A project document: discovery notes, the BR, design specs, meeting minutes, client files.
/// The text of PDF / Word / PowerPoint / Excel / text files is extracted on upload so Claude can read and search it.
/// </summary>
public class ProjectDocument : BaseEntity, ISourceTracked
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public ProjectDocumentCategory Category { get; set; } = ProjectDocumentCategory.Other;
    public string FileName { get; set; } = string.Empty;
    /// <summary>Random server-side name; files are stored outside the web root.</summary>
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public int UploadedByUserId { get; set; }
    public ApplicationUser UploadedBy { get; set; } = null!;
    /// <summary>Plain text of the document (null when it has none, e.g. images, or extraction failed).</summary>
    public string? ExtractedText { get; set; }
    public string? Source { get; set; }
}
