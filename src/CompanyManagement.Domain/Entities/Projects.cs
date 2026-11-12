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
