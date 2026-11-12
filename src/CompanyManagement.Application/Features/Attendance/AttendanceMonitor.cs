using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CompanyManagement.Application.Features.Attendance;

public record MonitorRunResult(int AbsentMarked, int OnLeaveMarked, int MissingCheckOutFlagged);

public interface IAttendanceMonitor
{
    /// <summary>
    /// Finalizes past working days: creates Absent / OnLeave records for employees without attendance and flags
    /// records that were never checked out. Idempotent; safe to run repeatedly.
    /// </summary>
    Task<MonitorRunResult> RunAsync(int lookbackDays = 3, CancellationToken ct = default);
}

public class AttendanceMonitor(
    IApplicationDbContext db,
    ICompanyClock clock,
    INotificationService notifications,
    ILogger<AttendanceMonitor> logger) : IAttendanceMonitor
{
    public async Task<MonitorRunResult> RunAsync(int lookbackDays = 3, CancellationToken ct = default)
    {
        var settings = await db.CompanySettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? new CompanySettings();
        var tz = await clock.GetTimeZoneAsync(ct);
        var now = clock.ToCompanyTime(clock.UtcNow, tz);
        var today = DateOnly.FromDateTime(now.DateTime);
        var from = today.AddDays(-lookbackDays);

        var employees = await AttendanceService.SlimAsync(db.Employees.Where(e => e.IsActive && !e.IsDeleted && e.HireDate <= today), ct);
        if (employees.Count == 0) return new MonitorRunResult(0, 0, 0);

        var calendar = await AttendanceCalendar.LoadAsync(db, from, today, null, ct);
        var existing = (await db.AttendanceRecords.Where(a => a.AttendanceDate >= from && a.AttendanceDate <= today)
            .Select(a => new { a.EmployeeId, a.AttendanceDate }).ToListAsync(ct))
            .Select(a => (a.EmployeeId, a.AttendanceDate)).ToHashSet();

        int absent = 0, leave = 0;
        foreach (var e in employees)
        {
            var schedule = calendar.ScheduleFor(e.WorkScheduleId);
            foreach (var day in AttendanceCalendar.Days(from, today))
            {
                if (day < e.HireDate || existing.Contains((e.Id, day))) continue;
                // The day is final only once the schedule end time + delay has passed.
                var finalizeAt = clock.ToCompanyTime(day, schedule.EndTime, tz).AddMinutes(settings.AttendanceFinalizeDelayMinutes);
                if (now < finalizeAt) continue;
                if (!calendar.IsWorkingDay(e.WorkScheduleId, day)) continue;

                var approved = calendar.ApprovedLeave(e.Id, day);
                if (approved is not null)
                {
                    db.AttendanceRecords.Add(new AttendanceRecord
                    {
                        EmployeeId = e.Id, AttendanceDate = day, Status = AttendanceStatus.OnLeave, CheckInSource = AttendanceSource.System,
                        Notes = $"Approved {approved.Type} leave #{approved.Id}"
                    });
                    leave++;
                    continue;
                }

                var pending = calendar.PendingLeave(e.Id, day);
                db.AttendanceRecords.Add(new AttendanceRecord
                {
                    EmployeeId = e.Id, AttendanceDate = day, Status = AttendanceStatus.Absent, CheckInSource = AttendanceSource.System,
                    Notes = pending is not null ? $"Pending {pending.Type} leave request #{pending.Id}" : null
                });
                absent++;
                // No "Marked absent" notification: the user found it of no value (the absence is still recorded and reported).
            }
        }

        // Records still open after the finalize window -> missing check-out.
        var open = await db.AttendanceRecords.Include(a => a.Employee)
            .Where(a => a.CheckInTime != null && a.CheckOutTime == null && !a.MissingCheckOut && a.AttendanceDate >= from.AddDays(-7))
            .ToListAsync(ct);
        int missing = 0;
        foreach (var a in open)
        {
            var schedule = calendar.ScheduleFor(a.Employee.WorkScheduleId);
            var finalizeAt = clock.ToCompanyTime(a.AttendanceDate, schedule.EndTime, tz).AddMinutes(settings.AttendanceFinalizeDelayMinutes);
            // Overnight work: never flag before 16h after check-in.
            var earliest = a.CheckInTime!.Value.AddHours(16);
            if (now < finalizeAt || (a.AttendanceDate < today ? false : now < earliest)) continue;
            a.MissingCheckOut = true;
            missing++;
            if (a.Employee.UserId is int uid)
                notifications.Add(uid, NotificationType.AttendanceAttention, "Missing check-out",
                    $"You did not check out on {a.AttendanceDate:yyyy-MM-dd}. Please ask HR to correct the record.",
                    "لم يتم تسجيل الانصراف", $"لم تسجّل الانصراف يوم {a.AttendanceDate:yyyy-MM-dd}. يرجى مراجعة الموارد البشرية.",
                    "/attendance/my", nameof(AttendanceRecord), a.Id);
            var managers = await ManagerLookup.GetManagerEmployeeIdsAsync(db, a.EmployeeId, ct);
            notifications.AddForEmployees(managers, NotificationType.AttendanceAttention, "Missing check-out",
                $"{a.Employee.FullName} did not check out on {a.AttendanceDate:yyyy-MM-dd}.",
                "انصراف غير مسجل", $"لم يسجّل {a.Employee.FullName} الانصراف يوم {a.AttendanceDate:yyyy-MM-dd}.",
                "/attendance/history", nameof(AttendanceRecord), a.Id);
        }

        if (absent + leave + missing > 0)
        {
            try
            {
                await notifications.SaveAndDispatchAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // A concurrent check-in may have created a record for the same day; the next run will reconcile.
                logger.LogWarning(ex, "Attendance monitor could not persist results; will retry on next run.");
                return new MonitorRunResult(0, 0, 0);
            }
            logger.LogInformation("Attendance monitor: {Absent} absent, {Leave} on leave, {Missing} missing check-outs",
                absent, leave, missing);
        }
        return new MonitorRunResult(absent, leave, missing);
    }
}
