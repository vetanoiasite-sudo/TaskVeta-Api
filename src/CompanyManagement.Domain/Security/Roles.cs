namespace CompanyManagement.Domain.Security;

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";       // HR / administration
    public const string Manager = "Manager";   // Manager / team leader
    public const string Employee = "Employee";

    public static readonly string[] All = [SuperAdmin, Admin, Manager, Employee];
}

/// <summary>Fine-grained permissions. Each maps to an authorization policy of the same name.</summary>
public static class Permissions
{
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string SettingsManage = "settings.manage";
    public const string DepartmentsManage = "departments.manage";
    public const string TeamsManage = "teams.manage";
    public const string EmployeesView = "employees.view";
    public const string EmployeesManage = "employees.manage";
    public const string AttendanceSelf = "attendance.self";
    public const string AttendanceViewTeam = "attendance.view.team";
    public const string AttendanceViewAll = "attendance.view.all";
    public const string AttendanceCorrect = "attendance.correct";
    public const string AttendanceKiosk = "attendance.kiosk";
    public const string LeaveApprove = "leave.approve";
    public const string HolidaysManage = "holidays.manage";
    public const string TasksAssign = "tasks.assign";
    public const string TasksReview = "tasks.review";
    public const string TasksViewAll = "tasks.view.all";
    public const string ReportsView = "reports.view";
    public const string ReportsExport = "reports.export";
    public const string AuditView = "audit.view";

    public static readonly string[] All =
    [
        UsersManage, RolesManage, SettingsManage, DepartmentsManage, TeamsManage, EmployeesView, EmployeesManage,
        AttendanceSelf, AttendanceViewTeam, AttendanceViewAll, AttendanceCorrect, AttendanceKiosk, LeaveApprove,
        HolidaysManage, TasksAssign, TasksReview, TasksViewAll, ReportsView, ReportsExport, AuditView
    ];

    public static readonly IReadOnlyDictionary<string, string[]> ByRole = new Dictionary<string, string[]>
    {
        [Roles.SuperAdmin] = All,
        [Roles.Admin] =
        [
            DepartmentsManage, TeamsManage, EmployeesView, EmployeesManage, AttendanceSelf, AttendanceViewTeam,
            AttendanceViewAll, AttendanceCorrect, AttendanceKiosk, LeaveApprove, HolidaysManage, TasksAssign,
            TasksReview, TasksViewAll, ReportsView, ReportsExport, AuditView
        ],
        [Roles.Manager] =
        [
            EmployeesView, AttendanceSelf, AttendanceViewTeam, LeaveApprove, TasksAssign, TasksReview, ReportsView
        ],
        [Roles.Employee] = [AttendanceSelf],
    };

    public static IEnumerable<string> ForRoles(IEnumerable<string> roles) =>
        roles.SelectMany(r => ByRole.TryGetValue(r, out var p) ? p : []).Distinct();
}
