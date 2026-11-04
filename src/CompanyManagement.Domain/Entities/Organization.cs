using CompanyManagement.Domain.Common;

namespace CompanyManagement.Domain.Entities;

public class Department : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public int? ManagerEmployeeId { get; set; }
    public Employee? Manager { get; set; }

    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
    public ICollection<Team> Teams { get; set; } = new List<Team>();
}

public class Team : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;

    public int? LeaderEmployeeId { get; set; }
    public Employee? Leader { get; set; }

    public ICollection<TeamMember> Members { get; set; } = new List<TeamMember>();
}

public class TeamMember : BaseEntity
{
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public DateOnly JoinedOn { get; set; }
}

public class Employee : BaseEntity
{
    public string EmployeeCode { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName => $"{FirstName} {LastName}".Trim();
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? JobTitle { get; set; }
    public DateOnly HireDate { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }

    /// <summary>When true the employee may check in without on-site verification (approved remote worker).</summary>
    public bool AllowRemoteCheckIn { get; set; }

    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;

    public int? WorkScheduleId { get; set; }
    public WorkSchedule? WorkSchedule { get; set; }

    public int? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public ICollection<TeamMember> TeamMemberships { get; set; } = new List<TeamMember>();
    public ICollection<AttendanceRecord> AttendanceRecords { get; set; } = new List<AttendanceRecord>();
    public ICollection<LeaveRequest> LeaveRequests { get; set; } = new List<LeaveRequest>();
}
