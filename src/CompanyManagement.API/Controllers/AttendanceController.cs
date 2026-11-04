using CompanyManagement.API.Infrastructure;
using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Attendance;
using CompanyManagement.Application.Features.Settings;
using CompanyManagement.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CompanyManagement.API.Controllers;

[ApiController]
[Route("api/attendance")]
public class AttendanceController(IAttendanceService attendance, IExportService export) : ControllerBase
{
    // ---------------- self service ----------------

    /// <summary>Today's attendance state for the current employee, including which on-site verification is required.</summary>
    [HttpGet("me/status")]
    [Authorize(Policy = Permissions.AttendanceSelf)]
    public async Task<ActionResult<MyAttendanceStatusDto>> MyStatus(CancellationToken ct) => Ok(await attendance.GetMyStatusAsync(ct));

    [HttpGet("me/history")]
    [Authorize(Policy = Permissions.AttendanceSelf)]
    public async Task<ActionResult<PagedResult<AttendanceRecordDto>>> MyHistory([FromQuery] AttendanceQuery query, CancellationToken ct) =>
        Ok(await attendance.GetEmployeeHistoryAsync(await CurrentEmployeeIdAsync(ct), query, ct));

    [HttpPost("check-in")]
    [Authorize(Policy = Permissions.AttendanceSelf)]
    [EnableRateLimiting("attendance")]
    public async Task<ActionResult<AttendanceRecordDto>> CheckIn(CheckRequest request, CancellationToken ct) =>
        Ok(await attendance.CheckInAsync(request, ct));

    [HttpPost("check-out")]
    [Authorize(Policy = Permissions.AttendanceSelf)]
    [EnableRateLimiting("attendance")]
    public async Task<ActionResult<AttendanceRecordDto>> CheckOut(CheckRequest request, CancellationToken ct) =>
        Ok(await attendance.CheckOutAsync(request, ct));

    // ---------------- monitoring (managers / HR) ----------------

    [HttpGet]
    [Authorize(Policy = Permissions.AttendanceViewTeam)]
    public async Task<ActionResult<PagedResult<AttendanceRecordDto>>> Get([FromQuery] AttendanceQuery query, CancellationToken ct) =>
        Ok(await attendance.GetPagedAsync(query, ct));

    [HttpGet("export")]
    [Authorize(Policy = Permissions.ReportsExport)]
    public async Task<IActionResult> Export([FromQuery] AttendanceQuery query, [FromQuery] string format = "xlsx", CancellationToken ct = default)
    {
        var rows = await attendance.GetAllForExportAsync(query, ct);
        return ExportResults.File(this, export, format, "attendance", "Attendance Records",
            $"{query.From:yyyy-MM-dd} – {query.To:yyyy-MM-dd}", rows, ReportColumns.Attendance);
    }

    [HttpGet("daily")]
    [Authorize(Policy = Permissions.AttendanceViewTeam)]
    public async Task<ActionResult<DailyAttendanceDto>> Daily([FromQuery] DailyQuery query, CancellationToken ct) =>
        Ok(await attendance.GetDailyAsync(query, ct));

    [HttpGet("daily/summary")]
    [Authorize(Policy = Permissions.AttendanceViewTeam)]
    public async Task<ActionResult<DailySummaryDto>> DailySummary([FromQuery] DateOnly? date, CancellationToken ct) =>
        Ok(await attendance.GetDailySummaryAsync(date, ct));

    [HttpGet("absent")]
    [Authorize(Policy = Permissions.AttendanceViewTeam)]
    public async Task<ActionResult<PagedResult<AbsenceDto>>> Absent([FromQuery] AbsenceQuery query, CancellationToken ct) =>
        Ok(await attendance.GetAbsencesAsync(query, ct));

    [HttpGet("employee/{employeeId:int}")]
    public async Task<ActionResult<PagedResult<AttendanceRecordDto>>> EmployeeHistory(int employeeId, [FromQuery] AttendanceQuery query, CancellationToken ct) =>
        Ok(await attendance.GetEmployeeHistoryAsync(employeeId, query, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AttendanceDetailsDto>> Details(int id, CancellationToken ct) => Ok(await attendance.GetDetailsAsync(id, ct));

    /// <summary>HR correction. A reason is mandatory and the change is written to the audit log.</summary>
    [HttpPut("{id:int}")]
    [Authorize(Policy = Permissions.AttendanceCorrect)]
    public async Task<ActionResult<AttendanceRecordDto>> Correct(int id, CorrectAttendanceRequest request, CancellationToken ct) =>
        Ok(await attendance.CorrectAsync(id, request, ct));

    [HttpPost("manual")]
    [Authorize(Policy = Permissions.AttendanceCorrect)]
    public async Task<ActionResult<AttendanceRecordDto>> CreateManual(CreateManualAttendanceRequest request, CancellationToken ct) =>
        Ok(await attendance.CreateManualAsync(request, ct));

    /// <summary>Rotating QR code for the entrance kiosk screen.</summary>
    [HttpGet("kiosk/qr")]
    [Authorize(Policy = Permissions.AttendanceKiosk)]
    public async Task<ActionResult<QrCodeDto>> KioskQr(CancellationToken ct) => Ok(await attendance.GetKioskQrAsync(ct));

    private async Task<int> CurrentEmployeeIdAsync(CancellationToken ct)
    {
        var scope = HttpContext.RequestServices.GetRequiredService<IAccessScope>();
        return await scope.GetCurrentEmployeeIdAsync(ct) ?? throw new BadRequestException("Your account is not linked to an employee profile.");
    }
}

[ApiController]
[Route("api/leave-requests")]
public class LeaveRequestsController(ILeaveService leave) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<LeaveRequestDto>>> Get([FromQuery] LeaveQuery query, CancellationToken ct) =>
        Ok(await leave.GetPagedAsync(query, ct));

    [HttpPost]
    public async Task<ActionResult<LeaveRequestDto>> Create(CreateLeaveRequest request, CancellationToken ct) => Ok(await leave.CreateAsync(request, ct));

    [HttpPost("{id:int}/review")]
    [Authorize(Policy = Permissions.LeaveApprove)]
    public async Task<ActionResult<LeaveRequestDto>> Review(int id, ReviewLeaveRequest request, CancellationToken ct) =>
        Ok(await leave.ReviewAsync(id, request, ct));

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        await leave.CancelAsync(id, ct);
        return NoContent();
    }
}

public static class ReportColumns
{
    public static readonly IReadOnlyList<ExportColumn<AttendanceRecordDto>> Attendance =
    [
        new("Date", r => r.AttendanceDate),
        new("Code", r => r.EmployeeCode),
        new("Employee", r => r.EmployeeName),
        new("Department", r => r.DepartmentName),
        new("Check-in", r => r.CheckInTime?.ToString("HH:mm")),
        new("Check-out", r => r.CheckOutTime?.ToString("HH:mm")),
        new("Hours", r => r.WorkingHours),
        new("Late (min)", r => r.LateMinutes),
        new("Early leave (min)", r => r.EarlyLeaveMinutes),
        new("Status", r => r.Status.ToString()),
        new("Missing check-out", r => r.MissingCheckOut),
        new("Edited", r => r.IsManuallyEdited),
        new("Notes", r => r.Notes),
    ];

    public static readonly IReadOnlyList<ExportColumn<AbsenceDto>> Absence =
    [
        new("Date", r => r.Date),
        new("Code", r => r.EmployeeCode),
        new("Employee", r => r.EmployeeName),
        new("Department", r => r.DepartmentName),
        new("Status", r => r.AbsenceStatus),
        new("Reason", r => r.Reason),
    ];
}
