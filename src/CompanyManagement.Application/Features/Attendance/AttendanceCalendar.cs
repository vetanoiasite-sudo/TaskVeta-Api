using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Attendance;

/// <summary>Minimal employee data needed for attendance calculations.</summary>
public record EmployeeSlim(int Id, string EmployeeCode, string Name, int DepartmentId, string DepartmentName, int? WorkScheduleId,
    DateOnly HireDate, int? UserId, bool AllowRemoteCheckIn);

/// <summary>
/// Working-day calendar for a date range: schedules, weekends, holidays and approved/pending leave.
/// </summary>
public class AttendanceCalendar
{
    private readonly Dictionary<int, WorkSchedule> _schedules;
    private readonly WorkSchedule _default;
    private readonly List<Holiday> _holidays;
    private readonly ILookup<int, LeaveRequest> _leaves;

    private AttendanceCalendar(Dictionary<int, WorkSchedule> schedules, WorkSchedule @default, List<Holiday> holidays, ILookup<int, LeaveRequest> leaves)
    {
        _schedules = schedules;
        _default = @default;
        _holidays = holidays;
        _leaves = leaves;
    }

    public static async Task<AttendanceCalendar> LoadAsync(IApplicationDbContext db, DateOnly from, DateOnly to,
        IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var schedules = await db.WorkSchedules.AsNoTracking().ToDictionaryAsync(w => w.Id, ct);
        var @default = schedules.Values.FirstOrDefault(s => s.IsDefault) ?? schedules.Values.FirstOrDefault()
                       ?? new WorkSchedule { Name = "Standard", IsDefault = true };
        var holidays = await db.Holidays.AsNoTracking().Where(h => h.StartDate <= to && h.EndDate >= from).ToListAsync(ct);
        var leaveQuery = db.LeaveRequests.AsNoTracking()
            .Where(l => (l.Status == LeaveStatus.Approved || l.Status == LeaveStatus.Pending) && l.StartDate <= to && l.EndDate >= from);
        if (employeeIds is not null && employeeIds.Count <= 2000)
            leaveQuery = leaveQuery.Where(l => employeeIds.Contains(l.EmployeeId));
        var leaves = (await leaveQuery.ToListAsync(ct)).ToLookup(l => l.EmployeeId);
        return new AttendanceCalendar(schedules, @default, holidays, leaves);
    }

    public WorkSchedule ScheduleFor(int? workScheduleId) =>
        workScheduleId is int id && _schedules.TryGetValue(id, out var s) ? s : _default;

    public Holiday? HolidayOn(DateOnly date) => _holidays.FirstOrDefault(h => h.Covers(date));

    public bool IsWeekend(int? workScheduleId, DateOnly date) => ScheduleFor(workScheduleId).IsWeekend(date.DayOfWeek);

    public bool IsWorkingDay(int? workScheduleId, DateOnly date) => !IsWeekend(workScheduleId, date) && HolidayOn(date) is null;

    public LeaveRequest? ApprovedLeave(int employeeId, DateOnly date) =>
        _leaves[employeeId].FirstOrDefault(l => l.Status == LeaveStatus.Approved && date >= l.StartDate && date <= l.EndDate);

    public LeaveRequest? PendingLeave(int employeeId, DateOnly date) =>
        _leaves[employeeId].FirstOrDefault(l => l.Status == LeaveStatus.Pending && date >= l.StartDate && date <= l.EndDate);

    /// <summary>True if the employee is expected to be at work on the given day.</summary>
    public bool IsExpected(EmployeeSlim e, DateOnly date) =>
        date >= e.HireDate && IsWorkingDay(e.WorkScheduleId, date) && ApprovedLeave(e.Id, date) is null;

    public static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1)) yield return d;
    }
}
