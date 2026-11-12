using CompanyManagement.Domain.Common;
using CompanyManagement.Domain.Enums;

namespace CompanyManagement.Domain.Entities;

public class AttendanceRecord : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    /// <summary>The working day in the company's time zone.</summary>
    public DateOnly AttendanceDate { get; set; }
    /// <summary>1 by default; greater than 1 only when multiple shifts are enabled.</summary>
    public int ShiftNumber { get; set; } = 1;

    /// <summary>Stored with the company time-zone offset.</summary>
    public DateTimeOffset? CheckInTime { get; set; }
    public DateTimeOffset? CheckOutTime { get; set; }

    public decimal WorkingHours { get; set; }
    public int LateMinutes { get; set; }
    public int EarlyLeaveMinutes { get; set; }
    public AttendanceStatus Status { get; set; }
    public string? Notes { get; set; }

    /// <summary>Set by the monitoring job when the working day ended without a check-out.</summary>
    public bool MissingCheckOut { get; set; }

    public AttendanceSource CheckInSource { get; set; } = AttendanceSource.Web;
    public VerificationMethod CheckInVerification { get; set; }
    public string? CheckInIp { get; set; }
    public double? CheckInLatitude { get; set; }
    public double? CheckInLongitude { get; set; }

    public VerificationMethod CheckOutVerification { get; set; }
    public string? CheckOutIp { get; set; }
    public double? CheckOutLatitude { get; set; }
    public double? CheckOutLongitude { get; set; }

    public bool IsManuallyEdited { get; set; }
    public int? LastEditedByUserId { get; set; }

    /// <summary>Optimistic-concurrency token (PostgreSQL system column <c>xmin</c>).</summary>
    public uint RowVersion { get; set; }
}

public class WorkSchedule : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public TimeOnly StartTime { get; set; } = new(9, 0);
    public TimeOnly EndTime { get; set; } = new(17, 0);
    /// <summary>Minutes after StartTime before an arrival counts as late.</summary>
    public int LateGraceMinutes { get; set; } = 15;
    /// <summary>Minutes before EndTime within which leaving is not an early leave.</summary>
    public int EarlyLeaveGraceMinutes { get; set; } = 10;
    /// <summary>Worked hours below this (but above zero) mark the day as HalfDay.</summary>
    public decimal HalfDayThresholdHours { get; set; } = 4;
    /// <summary>Bitmask of <see cref="DayOfWeek"/> values (1 &lt;&lt; (int)day). Default Friday+Saturday.</summary>
    public int WeekendDaysMask { get; set; } = (1 << (int)DayOfWeek.Friday) | (1 << (int)DayOfWeek.Saturday);
    public bool IsDefault { get; set; }

    public bool IsWeekend(DayOfWeek day) => (WeekendDaysMask & (1 << (int)day)) != 0;

    public IEnumerable<DayOfWeek> WeekendDays =>
        Enum.GetValues<DayOfWeek>().Where(IsWeekend);

    public static int ToMask(IEnumerable<DayOfWeek> days) => days.Aggregate(0, (m, d) => m | (1 << (int)d));

    public decimal ScheduledHours => (decimal)(EndTime - StartTime).TotalHours;
}

public class Holiday : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? Description { get; set; }

    public bool Covers(DateOnly date) => date >= StartDate && date <= EndDate;
}

public class LeaveRequest : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public LeaveType Type { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? Reason { get; set; }
    public LeaveStatus Status { get; set; } = LeaveStatus.Pending;
    public int? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNotes { get; set; }
}

/// <summary>Single-row table holding company-wide configuration.</summary>
public class CompanySettings : BaseEntity
{
    public string CompanyName { get; set; } = "Company HQ";
    public string TimeZoneId { get; set; } = "UTC";
    public bool AllowMultipleShifts { get; set; }
    public int MaxShiftsPerDay { get; set; } = 1;
    public int TaskDeadlineReminderHours { get; set; } = 24;

    /// <summary>Default length of a new sprint in days (1 week unless the company changes it).</summary>
    public int SprintLengthDays { get; set; } = 7;
    /// <summary>Optional capacity target for sprints (% of estimated days). All null = no target; otherwise the three add up to 100.</summary>
    public int? CapacityNewPercent { get; set; }
    public int? CapacityImprovePercent { get; set; }
    public int? CapacityBugsPercent { get; set; }
    /// <summary>Minutes after the schedule end time before missing check-outs / absences are finalized.</summary>
    public int AttendanceFinalizeDelayMinutes { get; set; } = 120;

    // ---- On-site verification ----
    public VerificationPolicy VerificationPolicy { get; set; } = VerificationPolicy.Any;
    public bool RequireVerificationOnCheckOut { get; set; } = true;

    public bool GeofenceEnabled { get; set; }
    public double? OfficeLatitude { get; set; }
    public double? OfficeLongitude { get; set; }
    public int GeofenceRadiusMeters { get; set; } = 150;
    /// <summary>Reported GPS accuracy worse than this is rejected.</summary>
    public int MaxLocationAccuracyMeters { get; set; } = 100;

    public bool NetworkCheckEnabled { get; set; }
    /// <summary>Comma / newline separated IP addresses or CIDR ranges, e.g. "10.0.0.0/8, 192.168.1.0/24".</summary>
    public string? AllowedIpRanges { get; set; }

    public bool QrCodeEnabled { get; set; }
    public int QrRotationSeconds { get; set; } = 30;
    /// <summary>HMAC key for QR tokens; never returned by the API.</summary>
    public string QrSecret { get; set; } = string.Empty;

    public VerificationMethod EnabledMethods =>
        (GeofenceEnabled ? VerificationMethod.Geofence : 0) |
        (NetworkCheckEnabled ? VerificationMethod.Network : 0) |
        (QrCodeEnabled ? VerificationMethod.QrCode : 0);
}
