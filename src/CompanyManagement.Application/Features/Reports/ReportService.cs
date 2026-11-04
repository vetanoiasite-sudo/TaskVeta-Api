using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Attendance;
using CompanyManagement.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Reports;

public class ReportQuery
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public DateOnly? CompareFrom { get; set; }
    public DateOnly? CompareTo { get; set; }
    public int? DepartmentId { get; set; }
    public int? TeamId { get; set; }
    public int? EmployeeId { get; set; }
    /// <summary>json (default), xlsx or pdf.</summary>
    public string? Format { get; set; }
}

public class ReportQueryValidator : AbstractValidator<ReportQuery>
{
    public ReportQueryValidator()
    {
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.From <= x.To).WithMessage("'From' must be before 'To'.");
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.To.Value.DayNumber - x.From.Value.DayNumber <= 366)
            .WithMessage("Date range cannot exceed one year.");
        RuleFor(x => x).Must(x => x.CompareFrom is null || x.CompareTo is null || x.CompareFrom <= x.CompareTo)
            .WithMessage("'CompareFrom' must be before 'CompareTo'.");
        RuleFor(x => x.Format).Must(f => f is null or "json" or "xlsx" or "pdf").WithMessage("Format must be json, xlsx or pdf.");
    }
}

public record EmployeeAttendanceSummaryDto(int EmployeeId, string EmployeeCode, string EmployeeName, string DepartmentName,
    int ExpectedDays, int PresentDays, int AbsentDays, int LateDays, int TotalLateMinutes, int EarlyLeaveDays, int HalfDays,
    int OnLeaveDays, int WorkFromHomeDays, int MissingCheckOuts, decimal TotalHours, decimal AverageHours, double AttendanceRate,
    double PunctualityRate);

public record AttendanceTotalsDto(DateOnly From, DateOnly To, int Employees, int ExpectedDays, int PresentDays, int AbsentDays,
    int LateDays, int EarlyLeaveDays, int OnLeaveDays, decimal TotalHours, double AttendanceRate, double PunctualityRate);

public record DailyTrendPointDto(DateOnly Date, int Present, int Absent, int Late, int OnLeave);

public record DepartmentAttendanceDto(int DepartmentId, string DepartmentName, int Employees, int PresentDays, int AbsentDays,
    int LateDays, double AttendanceRate);

public record AttendanceReportDto(AttendanceTotalsDto Totals, AttendanceTotalsDto? Comparison, IReadOnlyList<EmployeeAttendanceSummaryDto> Rows,
    IReadOnlyList<DailyTrendPointDto> Trend, IReadOnlyList<DepartmentAttendanceDto> ByDepartment);

public record TaskAssigneeStatsDto(int EmployeeId, string EmployeeCode, string EmployeeName, string DepartmentName, int Assigned,
    int Completed, int CompletedOnTime, int InProgress, int Overdue, double CompletionRate, double OnTimeRate, double? AverageCompletionDays);

public record TaskReportDto(int Total, IReadOnlyDictionary<string, int> ByStatus, IReadOnlyDictionary<string, int> ByPriority,
    IReadOnlyList<KeyValuePair<string, int>> ByDepartment, IReadOnlyList<TaskAssigneeStatsDto> ByAssignee, int Overdue,
    double CompletionRate, double OnTimeRate);

public record EmployeePerformanceDto(int EmployeeId, string EmployeeCode, string EmployeeName, string DepartmentName,
    double AttendanceRate, double PunctualityRate, decimal TotalHours, int AbsentDays, int LateDays, int TasksAssigned,
    int TasksCompleted, int TasksCompletedOnTime, int TasksOverdue, double TaskCompletionRate, double OnTimeRate, double Score);

public interface IReportService
{
    Task<AttendanceReportDto> AttendanceAsync(ReportQuery q, CancellationToken ct = default);
    Task<IReadOnlyList<AbsenceDto>> AbsenceAsync(ReportQuery q, CancellationToken ct = default);
    Task<TaskReportDto> TasksAsync(ReportQuery q, CancellationToken ct = default);
    Task<IReadOnlyList<EmployeePerformanceDto>> EmployeePerformanceAsync(ReportQuery q, CancellationToken ct = default);
}

public class ReportService(IApplicationDbContext db, IAccessScope scope, ICompanyClock clock, IAttendanceService attendance) : IReportService
{
    private async Task<(DateOnly From, DateOnly To)> RangeAsync(ReportQuery q, CancellationToken ct)
    {
        var today = await clock.TodayAsync(ct);
        var to = q.To ?? today;
        var from = q.From ?? new DateOnly(to.Year, to.Month, 1);
        return (from, to);
    }

    private async Task<List<EmployeeSlim>> EmployeesAsync(ReportQuery q, CancellationToken ct)
    {
        var query = (await scope.ScopedEmployeesAsync(ct)).Where(e => e.IsActive);
        if (q.DepartmentId is int d) query = query.Where(e => e.DepartmentId == d);
        if (q.TeamId is int t) query = query.Where(e => e.TeamMemberships.Any(m => m.TeamId == t));
        if (q.EmployeeId is int id) query = query.Where(e => e.Id == id);
        return await AttendanceService.SlimAsync(query, ct);
    }

    public async Task<AttendanceReportDto> AttendanceAsync(ReportQuery q, CancellationToken ct = default)
    {
        var (from, to) = await RangeAsync(q, ct);
        var employees = await EmployeesAsync(q, ct);
        var (rows, trend) = await ComputeAttendanceAsync(employees, from, to, q, ct);
        var totals = Totals(from, to, rows);

        AttendanceTotalsDto? comparison = null;
        if (q.CompareFrom is DateOnly cf && q.CompareTo is DateOnly cto)
        {
            var (cmpRows, _) = await ComputeAttendanceAsync(employees, cf, cto, q, ct);
            comparison = Totals(cf, cto, cmpRows);
        }

        var byDept = rows.GroupBy(r => r.DepartmentName).Select(g =>
        {
            var expected = g.Sum(r => r.ExpectedDays);
            var deptId = employees.First(e => e.DepartmentName == g.Key).DepartmentId;
            return new DepartmentAttendanceDto(deptId, g.Key, g.Count(), g.Sum(r => r.PresentDays), g.Sum(r => r.AbsentDays),
                g.Sum(r => r.LateDays), Rate(g.Sum(r => r.PresentDays), expected));
        }).OrderBy(d => d.DepartmentName).ToList();

        return new AttendanceReportDto(totals, comparison, rows, trend, byDept);
    }

    private async Task<(List<EmployeeAttendanceSummaryDto> Rows, List<DailyTrendPointDto> Trend)> ComputeAttendanceAsync(
        List<EmployeeSlim> employees, DateOnly from, DateOnly to, ReportQuery q, CancellationToken ct)
    {
        var today = await clock.TodayAsync(ct);
        var effectiveTo = to > today ? today : to;
        var ids = employees.Select(e => e.Id).ToList();
        var calendar = await AttendanceCalendar.LoadAsync(db, from, effectiveTo, ids, ct);

        var records = await db.AttendanceRecords.AsNoTracking()
            .Where(a => a.AttendanceDate >= from && a.AttendanceDate <= effectiveTo && ids.Contains(a.EmployeeId))
            .Select(a => new { a.EmployeeId, a.AttendanceDate, a.CheckInTime, a.Status, a.LateMinutes, a.EarlyLeaveMinutes, a.WorkingHours, a.MissingCheckOut, a.ShiftNumber })
            .ToListAsync(ct);
        var byEmp = records.ToLookup(r => r.EmployeeId);

        var absences = from <= effectiveTo
            ? await attendance.ComputeAbsencesAsync(new AbsenceQuery { From = from, To = effectiveTo, DepartmentId = q.DepartmentId, TeamId = q.TeamId, EmployeeId = q.EmployeeId }, ct)
            : [];
        var absentByEmp = absences.Where(a => a.AbsenceStatus != "NotCheckedIn").ToLookup(a => a.EmployeeId);

        var rows = new List<EmployeeAttendanceSummaryDto>();
        foreach (var e in employees)
        {
            var recs = byEmp[e.Id].ToList();
            var worked = recs.Where(r => r.CheckInTime != null).ToList();
            var days = worked.GroupBy(r => r.AttendanceDate).ToList();
            var expected = from <= effectiveTo ? AttendanceCalendar.Days(from, effectiveTo).Count(d => calendar.IsExpected(e, d)) : 0;
            var present = days.Count;
            var lateDays = days.Count(g => g.Any(r => r.LateMinutes > 0));
            var onLeave = from <= effectiveTo
                ? AttendanceCalendar.Days(from, effectiveTo).Count(d => d >= e.HireDate && calendar.IsWorkingDay(e.WorkScheduleId, d) && calendar.ApprovedLeave(e.Id, d) != null)
                : 0;
            var hours = worked.Sum(r => r.WorkingHours);
            rows.Add(new EmployeeAttendanceSummaryDto(e.Id, e.EmployeeCode, e.Name, e.DepartmentName, expected, present,
                absentByEmp[e.Id].Count(), lateDays, worked.Sum(r => r.LateMinutes), days.Count(g => g.Any(r => r.EarlyLeaveMinutes > 0)),
                days.Count(g => g.Any(r => r.Status == AttendanceStatus.HalfDay)), onLeave,
                days.Count(g => g.Any(r => r.Status == AttendanceStatus.WorkFromHome)), worked.Count(r => r.MissingCheckOut),
                hours, present == 0 ? 0 : Math.Round(hours / present, 2), Rate(present, expected), Rate(present - lateDays, present)));
        }

        var trend = new List<DailyTrendPointDto>();
        if (from <= effectiveTo)
        {
            var absentByDay = absences.ToLookup(a => a.Date);
            var workedByDay = records.Where(r => r.CheckInTime != null).ToLookup(r => r.AttendanceDate);
            foreach (var d in AttendanceCalendar.Days(from, effectiveTo))
            {
                var w = workedByDay[d].ToList();
                trend.Add(new DailyTrendPointDto(d, w.Select(r => r.EmployeeId).Distinct().Count(), absentByDay[d].Count(),
                    w.Where(r => r.LateMinutes > 0).Select(r => r.EmployeeId).Distinct().Count(),
                    employees.Count(e => d >= e.HireDate && calendar.IsWorkingDay(e.WorkScheduleId, d) && calendar.ApprovedLeave(e.Id, d) != null)));
            }
        }
        return (rows, trend);
    }

    private static AttendanceTotalsDto Totals(DateOnly from, DateOnly to, List<EmployeeAttendanceSummaryDto> rows)
    {
        var expected = rows.Sum(r => r.ExpectedDays);
        var present = rows.Sum(r => r.PresentDays);
        var late = rows.Sum(r => r.LateDays);
        return new AttendanceTotalsDto(from, to, rows.Count, expected, present, rows.Sum(r => r.AbsentDays), late,
            rows.Sum(r => r.EarlyLeaveDays), rows.Sum(r => r.OnLeaveDays), rows.Sum(r => r.TotalHours), Rate(present, expected), Rate(present - late, present));
    }

    public async Task<IReadOnlyList<AbsenceDto>> AbsenceAsync(ReportQuery q, CancellationToken ct = default)
    {
        var (from, to) = await RangeAsync(q, ct);
        var list = await attendance.ComputeAbsencesAsync(new AbsenceQuery { From = from, To = to, DepartmentId = q.DepartmentId, TeamId = q.TeamId, EmployeeId = q.EmployeeId }, ct);
        return list.OrderByDescending(a => a.Date).ThenBy(a => a.EmployeeName).ToList();
    }

    public async Task<TaskReportDto> TasksAsync(ReportQuery q, CancellationToken ct = default)
    {
        var stats = await TaskStatsAsync(q, ct);
        var now = clock.UtcNow;
        var tasks = stats.Tasks;
        var byStatus = Enum.GetValues<TaskItemStatus>().ToDictionary(s => s.ToString(), s => tasks.Count(t => t.Status == s));
        var byPriority = Enum.GetValues<TaskPriority>().ToDictionary(p => p.ToString(), p => tasks.Count(t => t.Priority == p));
        var byDept = tasks.GroupBy(t => t.DepartmentName ?? "—").Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .OrderByDescending(k => k.Value).ToList();
        var completed = tasks.Where(t => t.Status == TaskItemStatus.Completed).ToList();
        var onTime = completed.Count(t => t.DueDate is null || t.CompletionDate <= t.DueDate);
        var overdue = tasks.Count(t => t.Status is not (TaskItemStatus.Completed or TaskItemStatus.Cancelled) && t.DueDate < now);
        var active = tasks.Count(t => t.Status != TaskItemStatus.Cancelled);
        return new TaskReportDto(tasks.Count, byStatus, byPriority, byDept, stats.ByAssignee, overdue, Rate(completed.Count, active), Rate(onTime, completed.Count));
    }

    private record TaskRow(int Id, TaskItemStatus Status, TaskPriority Priority, DateTime? DueDate, DateTime? CompletionDate,
        DateTime CreatedAt, DateTime? StartDate, string? DepartmentName, int[] AssigneeIds);

    private async Task<(List<TaskRow> Tasks, List<TaskAssigneeStatsDto> ByAssignee)> TaskStatsAsync(ReportQuery q, CancellationToken ct)
    {
        var (from, to) = await RangeAsync(q, ct);
        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var employees = await EmployeesAsync(q, ct);
        var ids = employees.Select(e => e.Id).ToList();

        // Tasks created in the period OR due in the period, assigned to someone in scope.
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => (t.CreatedAt >= fromUtc && t.CreatedAt < toUtc) || (t.DueDate >= fromUtc && t.DueDate < toUtc))
            .Where(t => t.Assignments.Any(a => ids.Contains(a.EmployeeId)))
            .Select(t => new TaskRow(t.Id, t.Status, t.Priority, t.DueDate, t.CompletionDate, t.CreatedAt, t.StartDate,
                t.Department != null ? t.Department.Name : null, t.Assignments.Select(a => a.EmployeeId).ToArray()))
            .ToListAsync(ct);

        var now = clock.UtcNow;
        var byAssignee = employees.Select(e =>
        {
            var mine = tasks.Where(t => t.AssigneeIds.Contains(e.Id) && t.Status != TaskItemStatus.Cancelled).ToList();
            var done = mine.Where(t => t.Status == TaskItemStatus.Completed).ToList();
            var onTime = done.Count(t => t.DueDate is null || t.CompletionDate <= t.DueDate);
            var avgDays = done.Count == 0 ? (double?)null
                : Math.Round(done.Average(t => ((t.CompletionDate ?? now) - (t.StartDate ?? t.CreatedAt)).TotalDays), 1);
            return new TaskAssigneeStatsDto(e.Id, e.EmployeeCode, e.Name, e.DepartmentName, mine.Count, done.Count, onTime,
                mine.Count(t => t.Status == TaskItemStatus.InProgress),
                mine.Count(t => t.Status is not TaskItemStatus.Completed && t.DueDate < now),
                Rate(done.Count, mine.Count), Rate(onTime, done.Count), avgDays);
        }).Where(s => s.Assigned > 0).OrderByDescending(s => s.Assigned).ToList();

        return (tasks, byAssignee);
    }

    public async Task<IReadOnlyList<EmployeePerformanceDto>> EmployeePerformanceAsync(ReportQuery q, CancellationToken ct = default)
    {
        var att = await AttendanceAsync(new ReportQuery { From = q.From, To = q.To, DepartmentId = q.DepartmentId, TeamId = q.TeamId, EmployeeId = q.EmployeeId }, ct);
        var (_, byAssignee) = await TaskStatsAsync(q, ct);
        var taskMap = byAssignee.ToDictionary(t => t.EmployeeId);

        return att.Rows.Select(r =>
        {
            taskMap.TryGetValue(r.EmployeeId, out var t);
            var taskCompletion = t?.CompletionRate ?? 0;
            var onTime = t?.OnTimeRate ?? 0;
            // Weighted score: attendance 35%, punctuality 15%, task completion 30%, on-time delivery 20%.
            // When an employee had no tasks, the attendance components carry the full weight.
            var score = t is null
                ? 0.7 * r.AttendanceRate + 0.3 * r.PunctualityRate
                : 0.35 * r.AttendanceRate + 0.15 * r.PunctualityRate + 0.30 * taskCompletion + 0.20 * onTime;
            return new EmployeePerformanceDto(r.EmployeeId, r.EmployeeCode, r.EmployeeName, r.DepartmentName, r.AttendanceRate,
                r.PunctualityRate, r.TotalHours, r.AbsentDays, r.LateDays, t?.Assigned ?? 0, t?.Completed ?? 0, t?.CompletedOnTime ?? 0,
                t?.Overdue ?? 0, taskCompletion, onTime, Math.Round(score, 1));
        }).OrderByDescending(p => p.Score).ToList();
    }

    private static double Rate(int part, int whole) => whole <= 0 ? 0 : Math.Round(100.0 * Math.Max(0, part) / whole, 1);
}
