using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Attendance;
using CompanyManagement.Application.Features.Audit;
using CompanyManagement.Application.Features.Auth;
using CompanyManagement.Application.Features.Notifications;
using CompanyManagement.Application.Features.Organization;
using CompanyManagement.Application.Features.Projects;
using CompanyManagement.Application.Features.Reports;
using CompanyManagement.Application.Features.Settings;
using CompanyManagement.Application.Features.Tasks;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyManagement.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        services.AddScoped<IAccessScope, AccessScope>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IDepartmentService, DepartmentService>();
        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IWorkScheduleService, WorkScheduleService>();
        services.AddScoped<IHolidayService, HolidayService>();
        services.AddScoped<ILeaveService, LeaveService>();
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddScoped<IAttendanceMonitor, AttendanceMonitor>();
        services.AddScoped<ITaskService, TaskService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ITaskDeadlineMonitor, TaskDeadlineMonitor>();
        services.AddScoped<INotificationQueryService, NotificationQueryService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        return services;
    }
}
