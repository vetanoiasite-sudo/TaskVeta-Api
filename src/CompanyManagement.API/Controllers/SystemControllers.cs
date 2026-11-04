using CompanyManagement.API.Infrastructure;
using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Attendance;
using CompanyManagement.Application.Features.Audit;
using CompanyManagement.Application.Features.Notifications;
using CompanyManagement.Application.Features.Reports;
using CompanyManagement.Application.Features.Settings;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyManagement.API.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController(ISettingsService settings) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.SettingsManage)]
    public async Task<ActionResult<SettingsDto>> Get(CancellationToken ct) => Ok(await settings.GetAsync(ct));

    /// <summary>Non-sensitive settings needed by every client.</summary>
    [HttpGet("public")]
    public async Task<ActionResult<PublicSettingsDto>> GetPublic(CancellationToken ct) => Ok(await settings.GetPublicAsync(ct));

    [HttpPut]
    [Authorize(Policy = Permissions.SettingsManage)]
    public async Task<ActionResult<SettingsDto>> Update(UpdateSettingsRequest request, CancellationToken ct) => Ok(await settings.UpdateAsync(request, ct));

    /// <summary>Invalidates all QR codes currently displayed.</summary>
    [HttpPost("qr-secret/regenerate")]
    [Authorize(Policy = Permissions.SettingsManage)]
    public async Task<IActionResult> RegenerateQr(CancellationToken ct)
    {
        await settings.RegenerateQrSecretAsync(ct);
        return NoContent();
    }

    [HttpGet("timezones")]
    [Authorize(Policy = Permissions.SettingsManage)]
    public ActionResult<IReadOnlyList<TimeZoneDto>> TimeZones() => Ok(settings.GetTimeZones());

    /// <summary>Returns the caller's IP as seen by the server (helps admins configure network ranges).</summary>
    [HttpGet("my-ip")]
    [Authorize(Policy = Permissions.SettingsManage)]
    public ActionResult<object> MyIp([FromServices] ICurrentUser user) => Ok(new { ip = user.IpAddress });
}

[ApiController]
[Route("api/work-schedules")]
public class WorkSchedulesController(IWorkScheduleService schedules) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkScheduleDto>>> Get(CancellationToken ct) => Ok(await schedules.GetAllAsync(ct));

    [HttpPost]
    [Authorize(Policy = Permissions.SettingsManage)]
    public async Task<ActionResult<WorkScheduleDto>> Create(SaveWorkScheduleRequest request, CancellationToken ct) => Ok(await schedules.CreateAsync(request, ct));

    [HttpPut("{id:int}")]
    [Authorize(Policy = Permissions.SettingsManage)]
    public async Task<ActionResult<WorkScheduleDto>> Update(int id, SaveWorkScheduleRequest request, CancellationToken ct) => Ok(await schedules.UpdateAsync(id, request, ct));

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permissions.SettingsManage)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await schedules.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/holidays")]
public class HolidaysController(IHolidayService holidays) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<HolidayDto>>> Get([FromQuery] int? year, CancellationToken ct) => Ok(await holidays.GetAsync(year, ct));

    [HttpPost]
    [Authorize(Policy = Permissions.HolidaysManage)]
    public async Task<ActionResult<HolidayDto>> Create(SaveHolidayRequest request, CancellationToken ct) => Ok(await holidays.CreateAsync(request, ct));

    [HttpPut("{id:int}")]
    [Authorize(Policy = Permissions.HolidaysManage)]
    public async Task<ActionResult<HolidayDto>> Update(int id, SaveHolidayRequest request, CancellationToken ct) => Ok(await holidays.UpdateAsync(id, request, ct));

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permissions.HolidaysManage)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await holidays.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/notifications")]
public class NotificationsController(INotificationQueryService notifications) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<NotificationDto>>> Get([FromQuery] NotificationQuery query, CancellationToken ct) =>
        Ok(await notifications.GetMineAsync(query, ct));

    [HttpGet("unread-count")]
    public async Task<ActionResult<object>> UnreadCount(CancellationToken ct) => Ok(new { count = await notifications.UnreadCountAsync(ct) });

    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct)
    {
        await notifications.MarkReadAsync(id, ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        await notifications.MarkAllReadAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await notifications.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/reports")]
[Authorize(Policy = Permissions.ReportsView)]
public class ReportsController(IReportService reports, IExportService export, ICurrentUser user) : ControllerBase
{
    [HttpGet("attendance")]
    public async Task<IActionResult> Attendance([FromQuery] ReportQuery q, CancellationToken ct)
    {
        var data = await reports.AttendanceAsync(q, ct);
        if (IsJson(q)) return Ok(data);
        EnsureExport();
        return ExportResults.File(this, export, q.Format!, "attendance-summary", "Attendance Summary",
            $"{data.Totals.From:yyyy-MM-dd} – {data.Totals.To:yyyy-MM-dd} · Attendance rate {data.Totals.AttendanceRate}%", data.Rows,
            [
                new("Code", r => r.EmployeeCode), new("Employee", r => r.EmployeeName), new("Department", r => r.DepartmentName),
                new("Expected", r => r.ExpectedDays), new("Present", r => r.PresentDays), new("Absent", r => r.AbsentDays),
                new("Late days", r => r.LateDays), new("Late min", r => r.TotalLateMinutes), new("Early leave", r => r.EarlyLeaveDays),
                new("On leave", r => r.OnLeaveDays), new("WFH", r => r.WorkFromHomeDays), new("Hours", r => r.TotalHours),
                new("Attendance %", r => r.AttendanceRate), new("Punctuality %", r => r.PunctualityRate),
            ]);
    }

    [HttpGet("absence")]
    public async Task<IActionResult> Absence([FromQuery] ReportQuery q, CancellationToken ct)
    {
        var data = await reports.AbsenceAsync(q, ct);
        if (IsJson(q)) return Ok(data);
        EnsureExport();
        return ExportResults.File(this, export, q.Format!, "absence", "Absence Report", $"{q.From:yyyy-MM-dd} – {q.To:yyyy-MM-dd}", data, ReportColumns.Absence);
    }

    [HttpGet("tasks")]
    public async Task<IActionResult> Tasks([FromQuery] ReportQuery q, CancellationToken ct)
    {
        var data = await reports.TasksAsync(q, ct);
        if (IsJson(q)) return Ok(data);
        EnsureExport();
        return ExportResults.File(this, export, q.Format!, "task-report", "Task Report",
            $"Total {data.Total} · Completion {data.CompletionRate}% · On time {data.OnTimeRate}% · Overdue {data.Overdue}", data.ByAssignee,
            [
                new("Code", r => r.EmployeeCode), new("Employee", r => r.EmployeeName), new("Department", r => r.DepartmentName),
                new("Assigned", r => r.Assigned), new("Completed", r => r.Completed), new("On time", r => r.CompletedOnTime),
                new("In progress", r => r.InProgress), new("Overdue", r => r.Overdue), new("Completion %", r => r.CompletionRate),
                new("On-time %", r => r.OnTimeRate), new("Avg days", r => r.AverageCompletionDays),
            ]);
    }

    [HttpGet("employee-performance")]
    public async Task<IActionResult> Performance([FromQuery] ReportQuery q, CancellationToken ct)
    {
        var data = await reports.EmployeePerformanceAsync(q, ct);
        if (IsJson(q)) return Ok(data);
        EnsureExport();
        return ExportResults.File(this, export, q.Format!, "employee-performance", "Employee Performance", $"{q.From:yyyy-MM-dd} – {q.To:yyyy-MM-dd}", data,
            [
                new("Code", r => r.EmployeeCode), new("Employee", r => r.EmployeeName), new("Department", r => r.DepartmentName),
                new("Attendance %", r => r.AttendanceRate), new("Punctuality %", r => r.PunctualityRate), new("Hours", r => r.TotalHours),
                new("Absent", r => r.AbsentDays), new("Late", r => r.LateDays), new("Tasks", r => r.TasksAssigned),
                new("Completed", r => r.TasksCompleted), new("On time", r => r.TasksCompletedOnTime), new("Overdue", r => r.TasksOverdue),
                new("Score", r => r.Score),
            ]);
    }

    private static bool IsJson(ReportQuery q) => string.IsNullOrEmpty(q.Format) || q.Format == "json";

    private void EnsureExport()
    {
        if (!user.HasPermission(Permissions.ReportsExport)) throw new ForbiddenException("You are not allowed to export reports.");
    }
}

[ApiController]
[Route("api/dashboard")]
public class DashboardController(IAttendanceService attendance, ITaskService tasks, IReportService reports, ICurrentUser user, ICompanyClock clock)
    : ControllerBase
{
    public record DashboardDto(DailySummaryDto? Attendance, TaskDashboardDto Tasks, IReadOnlyList<DailyTrendPointDto> Trend, MyAttendanceStatusDto? Me);

    /// <summary>Main dashboard: today's attendance (scoped), task counters and a 14-day attendance trend.</summary>
    [HttpGet]
    public async Task<ActionResult<DashboardDto>> Get(CancellationToken ct)
    {
        DailySummaryDto? summary = null;
        IReadOnlyList<DailyTrendPointDto> trend = [];
        if (user.HasPermission(Permissions.AttendanceViewTeam))
        {
            summary = await attendance.GetDailySummaryAsync(null, ct);
            var today = await clock.TodayAsync(ct);
            trend = (await reports.AttendanceAsync(new ReportQuery { From = today.AddDays(-13), To = today }, ct)).Trend;
        }
        MyAttendanceStatusDto? me = null;
        try { me = await attendance.GetMyStatusAsync(ct); } catch (BadRequestException) { /* user not linked to an employee */ }
        var taskStats = await tasks.GetDashboardAsync(!user.HasPermission(Permissions.TasksAssign), ct);
        return Ok(new DashboardDto(summary, taskStats, trend, me));
    }
}

[ApiController]
[Route("api/audit-logs")]
[Authorize(Policy = Permissions.AuditView)]
public class AuditLogsController(IAuditQueryService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditLogDto>>> Get([FromQuery] AuditQuery query, CancellationToken ct) => Ok(await audit.GetPagedAsync(query, ct));
}
