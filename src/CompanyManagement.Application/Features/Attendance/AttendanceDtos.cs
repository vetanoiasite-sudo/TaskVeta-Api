using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Enums;
using FluentValidation;

namespace CompanyManagement.Application.Features.Attendance;

public record AttendanceRecordDto(int Id, int EmployeeId, string EmployeeCode, string EmployeeName, int DepartmentId,
    string DepartmentName, DateOnly AttendanceDate, int ShiftNumber, DateTimeOffset? CheckInTime, DateTimeOffset? CheckOutTime,
    decimal WorkingHours, int LateMinutes, int EarlyLeaveMinutes, AttendanceStatus Status, string? Notes, bool MissingCheckOut,
    AttendanceSource CheckInSource, VerificationMethod CheckInVerification, VerificationMethod CheckOutVerification,
    bool IsManuallyEdited, DateTime CreatedAt, DateTime UpdatedAt);

public record AttendanceDetailsDto(AttendanceRecordDto Record, string? CheckInIp, double? CheckInLatitude, double? CheckInLongitude,
    string? CheckOutIp, double? CheckOutLatitude, double? CheckOutLongitude, string? LastEditedBy);

public class AttendanceQuery : PagedRequest
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int? EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
    public string? EmployeeCode { get; set; }
    public int? DepartmentId { get; set; }
    public int? TeamId { get; set; }
    public AttendanceStatus? Status { get; set; }
    public bool? MissingCheckOut { get; set; }
    public bool? Late { get; set; }
    public bool? EarlyLeave { get; set; }
}

public class AttendanceQueryValidator : AbstractValidator<AttendanceQuery>
{
    public AttendanceQueryValidator()
    {
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.From <= x.To).WithMessage("'From' must be before 'To'.");
        RuleFor(x => x.EmployeeName).MaximumLength(150);
        RuleFor(x => x.EmployeeCode).MaximumLength(30);
    }
}

public record CheckRequest(double? Latitude, double? Longitude, double? Accuracy, string? QrToken, string? Notes, bool WorkFromHome = false);

public class CheckRequestValidator : AbstractValidator<CheckRequest>
{
    public CheckRequestValidator()
    {
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90).When(x => x.Latitude.HasValue);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180).When(x => x.Longitude.HasValue);
        RuleFor(x => x.Accuracy).GreaterThanOrEqualTo(0).When(x => x.Accuracy.HasValue);
        RuleFor(x => x.QrToken).MaximumLength(200);
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

public record CorrectAttendanceRequest(TimeOnly? CheckInTime, TimeOnly? CheckOutTime, bool CheckOutNextDay,
    AttendanceStatus? Status, string? Notes, string Reason);

public class CorrectAttendanceRequestValidator : AbstractValidator<CorrectAttendanceRequest>
{
    public CorrectAttendanceRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required for attendance corrections.").MaximumLength(500);
        RuleFor(x => x.Notes).MaximumLength(500);
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.CheckOutTime).NotNull().When(x => x.CheckOutNextDay);
        RuleFor(x => x).Must(x => x.CheckInTime.HasValue || !x.CheckOutTime.HasValue)
            .WithMessage("Check-out requires a check-in time.");
        RuleFor(x => x).Must(x => x.CheckOutNextDay || x.CheckInTime is null || x.CheckOutTime is null || x.CheckOutTime > x.CheckInTime)
            .WithMessage("Check-out must be after check-in.");
    }
}

public record CreateManualAttendanceRequest(int EmployeeId, DateOnly AttendanceDate, TimeOnly? CheckInTime, TimeOnly? CheckOutTime,
    bool CheckOutNextDay, AttendanceStatus? Status, string? Notes, string Reason);

public class CreateManualAttendanceRequestValidator : AbstractValidator<CreateManualAttendanceRequest>
{
    public CreateManualAttendanceRequestValidator()
    {
        RuleFor(x => x.EmployeeId).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Notes).MaximumLength(500);
        RuleFor(x => x).Must(x => x.CheckInTime.HasValue || x.Status.HasValue).WithMessage("Provide a check-in time or a status.");
        RuleFor(x => x).Must(x => x.CheckOutNextDay || x.CheckInTime is null || x.CheckOutTime is null || x.CheckOutTime > x.CheckInTime)
            .WithMessage("Check-out must be after check-in.");
    }
}

public record MyAttendanceStatusDto(DateOnly Today, bool IsWorkingDay, string? HolidayName, bool OnLeave, TimeOnly ScheduleStart,
    TimeOnly ScheduleEnd, AttendanceRecordDto? CurrentRecord, IReadOnlyList<AttendanceRecordDto> TodayRecords, bool CanCheckIn,
    bool CanCheckOut, VerificationRequirementsDto Verification);

public record VerificationRequirementsDto(bool Required, VerificationPolicy Policy, bool Geofence, bool Network, bool QrCode,
    bool RequiredOnCheckOut, bool RemoteAllowed);

/// <summary>Per-employee view of one day.</summary>
public record DailyAttendanceRowDto(int EmployeeId, string EmployeeCode, string EmployeeName, int DepartmentId, string DepartmentName,
    int? RecordId, DateTimeOffset? CheckInTime, DateTimeOffset? CheckOutTime, decimal WorkingHours, int LateMinutes,
    int EarlyLeaveMinutes, AttendanceStatus? Status, string State, bool IsInside, string? Notes);

public record DailySummaryDto(DateOnly Date, int TotalEmployees, int Expected, int Present, int Absent, int Late, int OnLeave,
    int WorkFromHome, int InsideNow, int EarlyLeave, int NotCheckedOut, double AttendancePercentage);

public record DailyAttendanceDto(DailySummaryDto Summary, PagedResult<DailyAttendanceRowDto> Rows);

public class DailyQuery : PagedRequest
{
    public DateOnly? Date { get; set; }
    public int? DepartmentId { get; set; }
    public int? TeamId { get; set; }
    /// <summary>Filter on the computed state: Present, Late, Absent, NotArrived, OnLeave, Holiday, Weekend, Inside, CheckedOut.</summary>
    public string? State { get; set; }
}

public class AbsenceQuery : PagedRequest
{
    public DateOnly? Date { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int? EmployeeId { get; set; }
    public int? DepartmentId { get; set; }
    public int? TeamId { get; set; }
    public string? EmployeeCode { get; set; }
}

public class AbsenceQueryValidator : AbstractValidator<AbsenceQuery>
{
    public AbsenceQueryValidator()
    {
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.From <= x.To).WithMessage("'From' must be before 'To'.");
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.To.Value.DayNumber - x.From.Value.DayNumber <= 366)
            .WithMessage("Date range cannot exceed one year.");
    }
}

public record AbsenceDto(int EmployeeId, string EmployeeCode, string EmployeeName, int DepartmentId, string DepartmentName,
    DateOnly Date, string AbsenceStatus, string? Reason, int? RecordId);

public record QrCodeDto(string Token, string Url, string PngBase64, DateTime ExpiresAt, int RotationSeconds);
