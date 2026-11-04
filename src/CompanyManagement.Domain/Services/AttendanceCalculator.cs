using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;

namespace CompanyManagement.Domain.Services;

/// <summary>
/// Pure attendance rules. All times passed in must already be expressed in the company time zone.
/// </summary>
public static class AttendanceCalculator
{
    /// <summary>Statuses that are not derived from check-in / check-out times.</summary>
    public static readonly AttendanceStatus[] NonTimeStatuses =
        [AttendanceStatus.Absent, AttendanceStatus.OnLeave, AttendanceStatus.Holiday];

    public static int CalculateLateMinutes(TimeOnly checkIn, WorkSchedule schedule)
    {
        var minutesAfterStart = (int)Math.Floor((checkIn - schedule.StartTime).TotalMinutes);
        // TimeOnly subtraction wraps around midnight; treat "before start" as on time.
        if (checkIn < schedule.StartTime) return 0;
        return minutesAfterStart > schedule.LateGraceMinutes ? minutesAfterStart : 0;
    }

    public static int CalculateEarlyLeaveMinutes(TimeOnly checkOut, WorkSchedule schedule)
    {
        if (checkOut >= schedule.EndTime) return 0;
        var minutesBeforeEnd = (int)Math.Floor((schedule.EndTime - checkOut).TotalMinutes);
        return minutesBeforeEnd > schedule.EarlyLeaveGraceMinutes ? minutesBeforeEnd : 0;
    }

    public static decimal CalculateWorkingHours(DateTimeOffset checkIn, DateTimeOffset checkOut)
    {
        if (checkOut <= checkIn) return 0;
        return Math.Round((decimal)(checkOut - checkIn).TotalHours, 2);
    }

    /// <summary>
    /// Recomputes LateMinutes, EarlyLeaveMinutes, WorkingHours and Status from the record's times.
    /// A WorkFromHome status is preserved; non-time statuses are preserved when there is no check-in.
    /// </summary>
    public static void Recalculate(AttendanceRecord record, WorkSchedule schedule)
    {
        if (record.CheckInTime is null)
        {
            record.LateMinutes = 0;
            record.EarlyLeaveMinutes = 0;
            record.WorkingHours = 0;
            if (!NonTimeStatuses.Contains(record.Status)) record.Status = AttendanceStatus.Absent;
            return;
        }

        var checkIn = record.CheckInTime.Value;
        // Only the first shift of the day is subject to lateness.
        record.LateMinutes = record.ShiftNumber == 1
            ? CalculateLateMinutes(TimeOnly.FromDateTime(checkIn.DateTime), schedule)
            : 0;

        var isWfh = record.Status == AttendanceStatus.WorkFromHome;

        if (record.CheckOutTime is null)
        {
            record.WorkingHours = 0;
            record.EarlyLeaveMinutes = 0;
            if (!isWfh) record.Status = record.LateMinutes > 0 ? AttendanceStatus.Late : AttendanceStatus.Present;
            return;
        }

        var checkOut = record.CheckOutTime.Value;
        record.WorkingHours = CalculateWorkingHours(checkIn, checkOut);
        record.EarlyLeaveMinutes = checkOut.Date > checkIn.Date
            ? 0
            : CalculateEarlyLeaveMinutes(TimeOnly.FromDateTime(checkOut.DateTime), schedule);
        record.MissingCheckOut = false;

        if (isWfh) return;

        if (record.WorkingHours < schedule.HalfDayThresholdHours)
            record.Status = AttendanceStatus.HalfDay;
        else if (record.LateMinutes > 0)
            record.Status = AttendanceStatus.Late;
        else if (record.EarlyLeaveMinutes > 0)
            record.Status = AttendanceStatus.EarlyLeave;
        else
            record.Status = AttendanceStatus.Present;
    }

    /// <summary>Determines whether the given date is a working day for a schedule.</summary>
    public static bool IsWorkingDay(DateOnly date, WorkSchedule schedule, IEnumerable<Holiday> holidays) =>
        !schedule.IsWeekend(date.DayOfWeek) && !holidays.Any(h => h.Covers(date));
}
