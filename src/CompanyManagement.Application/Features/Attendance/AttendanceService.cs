using System.Linq.Expressions;
using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Settings;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using CompanyManagement.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CompanyManagement.Application.Features.Attendance;

public interface IAttendanceService
{
    Task<MyAttendanceStatusDto> GetMyStatusAsync(CancellationToken ct = default);
    Task<AttendanceRecordDto> CheckInAsync(CheckRequest request, CancellationToken ct = default);
    Task<AttendanceRecordDto> CheckOutAsync(CheckRequest request, CancellationToken ct = default);
    Task<PagedResult<AttendanceRecordDto>> GetPagedAsync(AttendanceQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<AttendanceRecordDto>> GetAllForExportAsync(AttendanceQuery query, CancellationToken ct = default);
    Task<AttendanceDetailsDto> GetDetailsAsync(int id, CancellationToken ct = default);
    Task<PagedResult<AttendanceRecordDto>> GetEmployeeHistoryAsync(int employeeId, AttendanceQuery query, CancellationToken ct = default);
    Task<DailyAttendanceDto> GetDailyAsync(DailyQuery query, CancellationToken ct = default);
    Task<DailySummaryDto> GetDailySummaryAsync(DateOnly? date, CancellationToken ct = default);
    Task<PagedResult<AbsenceDto>> GetAbsencesAsync(AbsenceQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<AbsenceDto>> ComputeAbsencesAsync(AbsenceQuery query, CancellationToken ct = default);
    Task<AttendanceRecordDto> CorrectAsync(int id, CorrectAttendanceRequest request, CancellationToken ct = default);
    Task<AttendanceRecordDto> CreateManualAsync(CreateManualAttendanceRequest request, CancellationToken ct = default);
    Task<QrCodeDto> GetKioskQrAsync(CancellationToken ct = default);
}

public class AttendanceService(
    IApplicationDbContext db,
    IAccessScope scope,
    ICurrentUser currentUser,
    ICompanyClock clock,
    ISettingsService settingsService,
    IAttendanceVerifier verifier,
    IAuditService audit,
    INotificationService notifications,
    IConfiguration configuration) : IAttendanceService
{
    private static readonly Dictionary<string, Expression<Func<AttendanceRecord, object?>>> Sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["attendanceDate"] = a => a.AttendanceDate,
        ["employeeName"] = a => a.Employee.FirstName,
        ["employeeCode"] = a => a.Employee.EmployeeCode,
        ["departmentName"] = a => a.Employee.Department.Name,
        ["checkInTime"] = a => a.CheckInTime,
        ["checkOutTime"] = a => a.CheckOutTime,
        ["workingHours"] = a => a.WorkingHours,
        ["lateMinutes"] = a => a.LateMinutes,
        ["earlyLeaveMinutes"] = a => a.EarlyLeaveMinutes,
        ["status"] = a => a.Status,
    };

    public static readonly Expression<Func<AttendanceRecord, AttendanceRecordDto>> Projection = a => new AttendanceRecordDto(
        a.Id, a.EmployeeId, a.Employee.EmployeeCode, a.Employee.FirstName + " " + a.Employee.LastName, a.Employee.DepartmentId,
        a.Employee.Department.Name, a.AttendanceDate, a.ShiftNumber, a.CheckInTime, a.CheckOutTime, a.WorkingHours, a.LateMinutes,
        a.EarlyLeaveMinutes, a.Status, a.Notes, a.MissingCheckOut, a.CheckInSource, a.CheckInVerification, a.CheckOutVerification,
        a.IsManuallyEdited, a.CreatedAt, a.UpdatedAt);

    // ------------------------------------------------------------------ self service

    public async Task<MyAttendanceStatusDto> GetMyStatusAsync(CancellationToken ct = default)
    {
        var employee = await RequireCurrentEmployeeAsync(ct);
        var settings = await settingsService.GetEntityAsync(ct);
        var today = await clock.TodayAsync(ct);
        var calendar = await AttendanceCalendar.LoadAsync(db, today, today, [employee.Id], ct);
        var schedule = calendar.ScheduleFor(employee.WorkScheduleId);

        var records = await db.AttendanceRecords.Where(a => a.EmployeeId == employee.Id && a.AttendanceDate == today)
            .OrderBy(a => a.ShiftNumber).Select(Projection).ToListAsync(ct);
        var open = await FindOpenRecordAsync(employee.Id, today, ct);
        var openDto = open is null ? null : await db.AttendanceRecords.Where(a => a.Id == open.Id).Select(Projection).FirstAsync(ct);
        var current = openDto ?? records.LastOrDefault();

        var checkedInShifts = records.Count(r => r.CheckInTime != null);
        var maxShifts = settings.AllowMultipleShifts ? settings.MaxShiftsPerDay : 1;
        var canCheckIn = open is null && checkedInShifts < maxShifts;

        return new MyAttendanceStatusDto(today, calendar.IsWorkingDay(employee.WorkScheduleId, today), calendar.HolidayOn(today)?.Name,
            calendar.ApprovedLeave(employee.Id, today) is not null, schedule.StartTime, schedule.EndTime, current, records,
            canCheckIn, open is not null, BuildRequirements(settings, employee));
    }

    public async Task<AttendanceRecordDto> CheckInAsync(CheckRequest r, CancellationToken ct = default)
    {
        var employee = await RequireCurrentEmployeeAsync(ct);
        if (!employee.IsActive) throw new ForbiddenException("Your employee profile is inactive.");
        var settings = await settingsService.GetEntityAsync(ct);
        var tz = await clock.GetTimeZoneAsync(ct);
        var now = clock.ToCompanyTime(clock.UtcNow, tz);
        var today = DateOnly.FromDateTime(now.DateTime);

        if (await FindOpenRecordAsync(employee.Id, today, ct) is not null)
            throw new ConflictException("You are already checked in. Check out first.");

        var todays = await db.AttendanceRecords.Where(a => a.EmployeeId == employee.Id && a.AttendanceDate == today)
            .OrderBy(a => a.ShiftNumber).ToListAsync(ct);
        var used = todays.Where(a => a.CheckInTime != null).ToList();
        var maxShifts = settings.AllowMultipleShifts ? settings.MaxShiftsPerDay : 1;
        if (used.Count >= maxShifts)
            throw new ConflictException(maxShifts == 1
                ? "Attendance has already been recorded for today."
                : $"The maximum of {maxShifts} shifts per day has been reached.");

        var wfh = r.WorkFromHome && employee.AllowRemoteCheckIn;
        if (r.WorkFromHome && !employee.AllowRemoteCheckIn)
            throw new ForbiddenException("You are not allowed to work from home. Contact HR.");
        var verification = await VerifyOrThrowAsync(settings, employee, r, "CheckIn", bypass: wfh, ct);

        // Re-use a placeholder record (Absent / OnLeave materialized by the system without a check-in) for the first shift.
        var record = todays.FirstOrDefault(a => a.CheckInTime == null);
        if (record is null)
        {
            record = new AttendanceRecord { EmployeeId = employee.Id, AttendanceDate = today, ShiftNumber = (todays.Count == 0 ? 0 : todays.Max(a => a.ShiftNumber)) + 1 };
            db.AttendanceRecords.Add(record);
        }

        record.CheckInTime = now;
        record.CheckOutTime = null;
        record.Status = wfh ? AttendanceStatus.WorkFromHome : AttendanceStatus.Present;
        record.Notes = string.IsNullOrWhiteSpace(r.Notes) ? record.Notes : r.Notes.Trim();
        record.CheckInSource = verification.HasFlag(VerificationMethod.QrCode) ? AttendanceSource.QrCode : AttendanceSource.Web;
        record.CheckInVerification = verification;
        record.CheckInIp = currentUser.IpAddress;
        record.CheckInLatitude = r.Latitude;
        record.CheckInLongitude = r.Longitude;

        var calendar = await AttendanceCalendar.LoadAsync(db, today, today, [employee.Id], ct);
        AttendanceCalculator.Recalculate(record, calendar.ScheduleFor(employee.WorkScheduleId));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Unique index (EmployeeId, AttendanceDate, ShiftNumber) protects against concurrent double check-ins.
            throw new ConflictException("Attendance has already been recorded for today.");
        }

        if (record.LateMinutes > 0)
        {
            var managers = await ManagerLookup.GetManagerEmployeeIdsAsync(db, employee.Id, ct);
            notifications.AddForEmployees(managers, NotificationType.AttendanceAttention, "Late arrival",
                $"{employee.FullName} checked in {record.LateMinutes} minutes late.",
                "تأخر في الحضور", $"سجّل {employee.FullName} الحضور متأخراً {record.LateMinutes} دقيقة.",
                "/attendance/daily", nameof(AttendanceRecord), record.Id);
            await notifications.SaveAndDispatchAsync(ct);
        }

        return await db.AttendanceRecords.Where(a => a.Id == record.Id).Select(Projection).FirstAsync(ct);
    }

    public async Task<AttendanceRecordDto> CheckOutAsync(CheckRequest r, CancellationToken ct = default)
    {
        var employee = await RequireCurrentEmployeeAsync(ct);
        var settings = await settingsService.GetEntityAsync(ct);
        var tz = await clock.GetTimeZoneAsync(ct);
        var now = clock.ToCompanyTime(clock.UtcNow, tz);
        var today = DateOnly.FromDateTime(now.DateTime);

        var record = await FindOpenRecordAsync(employee.Id, today, ct)
                     ?? throw new ConflictException("You have not checked in, so you cannot check out.");

        var bypass = record.Status == AttendanceStatus.WorkFromHome || !settings.RequireVerificationOnCheckOut;
        var verification = await VerifyOrThrowAsync(settings, employee, r, "CheckOut", bypass, ct);

        record.CheckOutTime = now;
        record.CheckOutVerification = verification;
        record.CheckOutIp = currentUser.IpAddress;
        record.CheckOutLatitude = r.Latitude;
        record.CheckOutLongitude = r.Longitude;
        if (!string.IsNullOrWhiteSpace(r.Notes)) record.Notes = r.Notes.Trim();

        var calendar = await AttendanceCalendar.LoadAsync(db, record.AttendanceDate, record.AttendanceDate, [employee.Id], ct);
        AttendanceCalculator.Recalculate(record, calendar.ScheduleFor(employee.WorkScheduleId));
        await db.SaveChangesAsync(ct);
        return await db.AttendanceRecords.Where(a => a.Id == record.Id).Select(Projection).FirstAsync(ct);
    }

    // ------------------------------------------------------------------ queries

    public async Task<PagedResult<AttendanceRecordDto>> GetPagedAsync(AttendanceQuery q, CancellationToken ct = default) =>
        await (await BuildQueryAsync(q, ct)).ApplySort(q, Sorts, "attendanceDate", defaultDesc: true)
            .Select(Projection).ToPagedAsync(q, ct);

    public async Task<IReadOnlyList<AttendanceRecordDto>> GetAllForExportAsync(AttendanceQuery q, CancellationToken ct = default) =>
        await (await BuildQueryAsync(q, ct)).ApplySort(q, Sorts, "attendanceDate", defaultDesc: true)
            .Select(Projection).Take(50_000).ToListAsync(ct);

    public async Task<AttendanceDetailsDto> GetDetailsAsync(int id, CancellationToken ct = default)
    {
        var a = await db.AttendanceRecords.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Attendance", id);
        await scope.EnsureCanAccessEmployeeAsync(a.EmployeeId, ct);
        var dto = await db.AttendanceRecords.Where(x => x.Id == id).Select(Projection).FirstAsync(ct);
        var showSensitive = currentUser.HasPermission(Permissions.AttendanceViewAll) || a.EmployeeId == await scope.GetCurrentEmployeeIdAsync(ct);
        var editor = a.LastEditedByUserId is int uid ? await db.Users.Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefaultAsync(ct) : null;
        return showSensitive
            ? new AttendanceDetailsDto(dto, a.CheckInIp, a.CheckInLatitude, a.CheckInLongitude, a.CheckOutIp, a.CheckOutLatitude, a.CheckOutLongitude, editor)
            : new AttendanceDetailsDto(dto, null, null, null, null, null, null, editor);
    }

    public async Task<PagedResult<AttendanceRecordDto>> GetEmployeeHistoryAsync(int employeeId, AttendanceQuery q, CancellationToken ct = default)
    {
        await scope.EnsureCanAccessEmployeeAsync(employeeId, ct);
        q.EmployeeId = employeeId;
        return await GetPagedAsync(q, ct);
    }

    private async Task<IQueryable<AttendanceRecord>> BuildQueryAsync(AttendanceQuery q, CancellationToken ct)
    {
        var employees = await scope.ScopedEmployeesAsync(ct);
        var query = db.AttendanceRecords.AsNoTracking().Where(a => employees.Any(e => e.Id == a.EmployeeId));
        if (q.From is DateOnly f) query = query.Where(a => a.AttendanceDate >= f);
        if (q.To is DateOnly t) query = query.Where(a => a.AttendanceDate <= t);
        if (q.EmployeeId is int emp) query = query.Where(a => a.EmployeeId == emp);
        if (!string.IsNullOrWhiteSpace(q.EmployeeName))
        {
            var n = q.EmployeeName.Trim();
            query = query.Where(a => (a.Employee.FirstName + " " + a.Employee.LastName).Contains(n));
        }
        if (!string.IsNullOrWhiteSpace(q.EmployeeCode)) query = query.Where(a => a.Employee.EmployeeCode.Contains(q.EmployeeCode.Trim()));
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(a => (a.Employee.FirstName + " " + a.Employee.LastName).Contains(s) || a.Employee.EmployeeCode.Contains(s));
        }
        if (q.DepartmentId is int d) query = query.Where(a => a.Employee.DepartmentId == d);
        if (q.TeamId is int team) query = query.Where(a => a.Employee.TeamMemberships.Any(m => m.TeamId == team));
        if (q.Status is AttendanceStatus st) query = query.Where(a => a.Status == st);
        if (q.MissingCheckOut is bool mc) query = query.Where(a => a.MissingCheckOut == mc);
        if (q.Late == true) query = query.Where(a => a.LateMinutes > 0);
        if (q.EarlyLeave == true) query = query.Where(a => a.EarlyLeaveMinutes > 0);
        return query;
    }

    // ------------------------------------------------------------------ daily view

    public async Task<DailySummaryDto> GetDailySummaryAsync(DateOnly? date, CancellationToken ct = default) =>
        (await BuildDailyAsync(new DailyQuery { Date = date }, ct)).Summary;

    public async Task<DailyAttendanceDto> GetDailyAsync(DailyQuery q, CancellationToken ct = default)
    {
        var (summary, rows) = await BuildDailyAsync(q, ct);
        IEnumerable<DailyAttendanceRowDto> filtered = rows;
        if (!string.IsNullOrWhiteSpace(q.State))
        {
            filtered = q.State.ToLowerInvariant() switch
            {
                "present" => rows.Where(r => r.CheckInTime != null),
                "late" => rows.Where(r => r.LateMinutes > 0),
                "inside" => rows.Where(r => r.IsInside),
                "earlyleave" => rows.Where(r => r.EarlyLeaveMinutes > 0),
                _ => rows.Where(r => string.Equals(r.State, q.State, StringComparison.OrdinalIgnoreCase)),
            };
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
            filtered = filtered.Where(r => r.EmployeeName.Contains(q.Search, StringComparison.OrdinalIgnoreCase)
                                           || r.EmployeeCode.Contains(q.Search, StringComparison.OrdinalIgnoreCase));
        filtered = (q.SortBy?.ToLowerInvariant(), q.Descending) switch
        {
            ("employeecode", var desc) => desc ? filtered.OrderByDescending(r => r.EmployeeCode) : filtered.OrderBy(r => r.EmployeeCode),
            ("checkintime", var desc) => desc ? filtered.OrderByDescending(r => r.CheckInTime) : filtered.OrderBy(r => r.CheckInTime),
            ("lateminutes", var desc) => desc ? filtered.OrderByDescending(r => r.LateMinutes) : filtered.OrderBy(r => r.LateMinutes),
            ("departmentname", var desc) => desc ? filtered.OrderByDescending(r => r.DepartmentName) : filtered.OrderBy(r => r.DepartmentName),
            ("state", var desc) => desc ? filtered.OrderByDescending(r => r.State) : filtered.OrderBy(r => r.State),
            (_, var desc) => desc ? filtered.OrderByDescending(r => r.EmployeeName) : filtered.OrderBy(r => r.EmployeeName),
        };
        return new DailyAttendanceDto(summary, filtered.ToPaged(q));
    }

    private async Task<(DailySummaryDto Summary, List<DailyAttendanceRowDto> Rows)> BuildDailyAsync(DailyQuery q, CancellationToken ct)
    {
        var tz = await clock.GetTimeZoneAsync(ct);
        var now = clock.ToCompanyTime(clock.UtcNow, tz);
        var today = DateOnly.FromDateTime(now.DateTime);
        var date = q.Date ?? today;

        var employeesQuery = (await scope.ScopedEmployeesAsync(ct)).Where(e => e.IsActive && e.HireDate <= date);
        if (q.DepartmentId is int d) employeesQuery = employeesQuery.Where(e => e.DepartmentId == d);
        if (q.TeamId is int t) employeesQuery = employeesQuery.Where(e => e.TeamMemberships.Any(m => m.TeamId == t));
        var employees = await SlimAsync(employeesQuery, ct);
        var ids = employees.Select(e => e.Id).ToList();

        var calendar = await AttendanceCalendar.LoadAsync(db, date, date, ids, ct);
        var records = (await db.AttendanceRecords.AsNoTracking().Where(a => a.AttendanceDate == date && employeesQuery.Select(e => e.Id).Contains(a.EmployeeId)).ToListAsync(ct))
            .ToLookup(a => a.EmployeeId);

        var rows = new List<DailyAttendanceRowDto>(employees.Count);
        int expected = 0, present = 0, absent = 0, late = 0, onLeave = 0, wfh = 0, inside = 0, early = 0, notOut = 0;

        foreach (var e in employees)
        {
            var recs = records[e.Id].OrderBy(a => a.ShiftNumber).ToList();
            var withIn = recs.Where(a => a.CheckInTime != null).ToList();
            var first = withIn.FirstOrDefault();
            var last = withIn.LastOrDefault();
            var placeholder = recs.FirstOrDefault(a => a.CheckInTime == null);
            var schedule = calendar.ScheduleFor(e.WorkScheduleId);
            var isExpected = calendar.IsExpected(e, date);
            if (isExpected) expected++;

            string state;
            AttendanceStatus? status = first?.Status ?? placeholder?.Status;
            var isInside = last is { CheckOutTime: null } && last.Status != AttendanceStatus.WorkFromHome && date == today;

            if (first is not null)
            {
                present++;
                if (first.LateMinutes > 0) late++;
                if (withIn.Any(a => a.Status == AttendanceStatus.WorkFromHome)) wfh++;
                if (withIn.Any(a => a.EarlyLeaveMinutes > 0)) early++;
                if (last!.CheckOutTime is null)
                {
                    if (date < today || last.MissingCheckOut) { notOut++; state = "MissingCheckOut"; }
                    else state = last.Status == AttendanceStatus.WorkFromHome ? "WorkFromHome" : "Inside";
                }
                else state = "CheckedOut";
                if (isInside) inside++;
            }
            else if (calendar.ApprovedLeave(e.Id, date) is not null || placeholder?.Status == AttendanceStatus.OnLeave)
            {
                onLeave++; state = "OnLeave"; status ??= AttendanceStatus.OnLeave;
            }
            else if (calendar.HolidayOn(date) is not null) { state = "Holiday"; status ??= AttendanceStatus.Holiday; }
            else if (calendar.IsWeekend(e.WorkScheduleId, date)) state = "Weekend";
            else if (date > today) state = "Upcoming";
            else if (placeholder?.Status == AttendanceStatus.Absent || date < today ||
                     TimeOnly.FromDateTime(now.DateTime) > schedule.StartTime.AddMinutes(schedule.LateGraceMinutes))
            {
                absent++; state = "Absent"; status = AttendanceStatus.Absent;
            }
            else state = "NotArrived";

            rows.Add(new DailyAttendanceRowDto(e.Id, e.EmployeeCode, e.Name, e.DepartmentId, e.DepartmentName,
                first?.Id ?? placeholder?.Id, first?.CheckInTime, last?.CheckOutTime, withIn.Sum(a => a.WorkingHours),
                first?.LateMinutes ?? 0, last?.EarlyLeaveMinutes ?? 0, status, state, isInside, first?.Notes ?? placeholder?.Notes));
        }

        var pct = expected == 0 ? 0 : Math.Round(100.0 * Math.Min(present, expected) / expected, 1);
        var summary = new DailySummaryDto(date, employees.Count, expected, present, absent, late, onLeave, wfh, inside, early, notOut, pct);
        return (summary, rows);
    }

    // ------------------------------------------------------------------ absences

    public async Task<PagedResult<AbsenceDto>> GetAbsencesAsync(AbsenceQuery q, CancellationToken ct = default)
    {
        var list = await ComputeAbsencesAsync(q, ct);
        IEnumerable<AbsenceDto> sorted = (q.SortBy?.ToLowerInvariant(), q.Descending) switch
        {
            ("employeename", var desc) => desc ? list.OrderByDescending(a => a.EmployeeName) : list.OrderBy(a => a.EmployeeName),
            ("employeecode", var desc) => desc ? list.OrderByDescending(a => a.EmployeeCode) : list.OrderBy(a => a.EmployeeCode),
            ("departmentname", var desc) => desc ? list.OrderByDescending(a => a.DepartmentName) : list.OrderBy(a => a.DepartmentName),
            ("date", false) => list.OrderBy(a => a.Date).ThenBy(a => a.EmployeeName),
            _ => list.OrderByDescending(a => a.Date).ThenBy(a => a.EmployeeName),
        };
        return sorted.ToPaged(q);
    }

    /// <summary>
    /// Absences are derived, not only stored: every expected working day (excluding weekends, holidays and approved leave)
    /// without a check-in is an absence. Materialized Absent records (created by the monitoring job or HR) are included.
    /// </summary>
    public async Task<IReadOnlyList<AbsenceDto>> ComputeAbsencesAsync(AbsenceQuery q, CancellationToken ct = default)
    {
        var tz = await clock.GetTimeZoneAsync(ct);
        var now = clock.ToCompanyTime(clock.UtcNow, tz);
        var today = DateOnly.FromDateTime(now.DateTime);

        var from = q.Date ?? q.From ?? today;
        var to = q.Date ?? q.To ?? from;
        if (to > today) to = today;
        if (from > to) return [];
        if (to.DayNumber - from.DayNumber > 366) throw new BadRequestException("Date range cannot exceed one year.");

        var employeesQuery = (await scope.ScopedEmployeesAsync(ct)).Where(e => e.IsActive && e.HireDate <= to);
        if (q.EmployeeId is int empId) employeesQuery = employeesQuery.Where(e => e.Id == empId);
        if (q.DepartmentId is int d) employeesQuery = employeesQuery.Where(e => e.DepartmentId == d);
        if (q.TeamId is int t) employeesQuery = employeesQuery.Where(e => e.TeamMemberships.Any(m => m.TeamId == t));
        if (!string.IsNullOrWhiteSpace(q.EmployeeCode)) employeesQuery = employeesQuery.Where(e => e.EmployeeCode.Contains(q.EmployeeCode.Trim()));
        if (!string.IsNullOrWhiteSpace(q.Search))
            employeesQuery = employeesQuery.Where(e => (e.FirstName + " " + e.LastName).Contains(q.Search.Trim()) || e.EmployeeCode.Contains(q.Search.Trim()));

        var employees = await SlimAsync(employeesQuery, ct);
        var ids = employees.Select(e => e.Id).ToList();
        var calendar = await AttendanceCalendar.LoadAsync(db, from, to, ids, ct);

        var records = await db.AttendanceRecords.AsNoTracking()
            .Where(a => a.AttendanceDate >= from && a.AttendanceDate <= to && employeesQuery.Select(e => e.Id).Contains(a.EmployeeId))
            .Select(a => new { a.Id, a.EmployeeId, a.AttendanceDate, a.CheckInTime, a.Status, a.Notes })
            .ToListAsync(ct);
        var byKey = records.ToLookup(a => (a.EmployeeId, a.AttendanceDate));

        var nowTime = TimeOnly.FromDateTime(now.DateTime);
        var result = new List<AbsenceDto>();
        foreach (var e in employees)
        {
            var schedule = calendar.ScheduleFor(e.WorkScheduleId);
            foreach (var day in AttendanceCalendar.Days(from, to))
            {
                var recs = byKey[(e.Id, day)].ToList();
                if (recs.Any(r => r.CheckInTime != null)) continue;
                var stored = recs.FirstOrDefault();

                if (stored is not null && stored.Status != AttendanceStatus.Absent) continue; // OnLeave / Holiday placeholder
                if (stored is null)
                {
                    if (!calendar.IsExpected(e, day)) continue;
                    if (day == today && nowTime <= schedule.StartTime.AddMinutes(schedule.LateGraceMinutes)) continue;
                }

                var pending = calendar.PendingLeave(e.Id, day);
                var status = pending is not null ? "PendingLeave" : day == today && stored is null ? "NotCheckedIn" : "Absent";
                var reason = stored?.Notes ?? (pending is not null ? $"Pending {pending.Type} leave: {pending.Reason}".TrimEnd(' ', ':') : null);
                result.Add(new AbsenceDto(e.Id, e.EmployeeCode, e.Name, e.DepartmentId, e.DepartmentName, day, status, reason, stored?.Id));
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ corrections (HR)

    public async Task<AttendanceRecordDto> CorrectAsync(int id, CorrectAttendanceRequest r, CancellationToken ct = default)
    {
        var record = await db.AttendanceRecords.Include(a => a.Employee).FirstOrDefaultAsync(a => a.Id == id, ct)
                     ?? throw new NotFoundException("Attendance", id);
        await scope.EnsureCanAccessEmployeeAsync(record.EmployeeId, ct);
        var tz = await clock.GetTimeZoneAsync(ct);

        var old = Snapshot(record);
        if (r.Notes is not null) record.Notes = r.Notes.Trim();
        var calendar = await AttendanceCalendar.LoadAsync(db, record.AttendanceDate, record.AttendanceDate, [record.EmployeeId], ct);
        var schedule = calendar.ScheduleFor(record.Employee.WorkScheduleId);
        ApplyTimesAndStatus(record, r.CheckInTime, r.CheckOutTime, r.CheckOutNextDay, r.Status, schedule, tz);

        record.IsManuallyEdited = true;
        record.LastEditedByUserId = currentUser.UserId;
        audit.Log("Correct", nameof(AttendanceRecord), id, old, Snapshot(record), r.Reason);

        if (record.Employee.UserId is int uid && uid != currentUser.UserId)
            notifications.Add(uid, NotificationType.AttendanceUpdated, "Attendance record updated",
                $"Your attendance for {record.AttendanceDate:yyyy-MM-dd} was updated by {currentUser.UserName}. Reason: {r.Reason}",
                "تم تعديل سجل الحضور", $"تم تعديل حضورك ليوم {record.AttendanceDate:yyyy-MM-dd}. السبب: {r.Reason}",
                "/attendance/my", nameof(AttendanceRecord), id);

        try { await notifications.SaveAndDispatchAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The record was modified by someone else. Reload and try again."); }
        return await db.AttendanceRecords.Where(a => a.Id == id).Select(Projection).FirstAsync(ct);
    }

    public async Task<AttendanceRecordDto> CreateManualAsync(CreateManualAttendanceRequest r, CancellationToken ct = default)
    {
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == r.EmployeeId && !e.IsDeleted, ct)
                       ?? throw new NotFoundException("Employee", r.EmployeeId);
        await scope.EnsureCanAccessEmployeeAsync(employee.Id, ct);
        var today = await clock.TodayAsync(ct);
        if (r.AttendanceDate > today) throw new BadRequestException("Cannot record attendance for a future date.");

        var settings = await settingsService.GetEntityAsync(ct);
        var existing = await db.AttendanceRecords.Where(a => a.EmployeeId == employee.Id && a.AttendanceDate == r.AttendanceDate).ToListAsync(ct);
        var placeholder = existing.FirstOrDefault(a => a.CheckInTime == null);
        if (placeholder is not null)
            return await CorrectAsync(placeholder.Id, new CorrectAttendanceRequest(r.CheckInTime, r.CheckOutTime, r.CheckOutNextDay, r.Status, r.Notes, r.Reason), ct);
        var maxShifts = settings.AllowMultipleShifts ? settings.MaxShiftsPerDay : 1;
        if (existing.Count >= maxShifts) throw new ConflictException("A record already exists for this day. Edit it instead.");

        var tz = await clock.GetTimeZoneAsync(ct);
        var record = new AttendanceRecord
        {
            EmployeeId = employee.Id, AttendanceDate = r.AttendanceDate, ShiftNumber = existing.Count == 0 ? 1 : existing.Max(a => a.ShiftNumber) + 1,
            Status = AttendanceStatus.Present, Notes = r.Notes, CheckInSource = AttendanceSource.Manual,
            IsManuallyEdited = true, LastEditedByUserId = currentUser.UserId
        };
        var calendar = await AttendanceCalendar.LoadAsync(db, r.AttendanceDate, r.AttendanceDate, [employee.Id], ct);
        ApplyTimesAndStatus(record, r.CheckInTime, r.CheckOutTime, r.CheckOutNextDay, r.Status, calendar.ScheduleFor(employee.WorkScheduleId), tz);
        db.AttendanceRecords.Add(record);
        await db.SaveChangesAsync(ct);
        audit.Log("CreateManual", nameof(AttendanceRecord), record.Id, null, Snapshot(record), r.Reason);
        if (employee.UserId is int uid && uid != currentUser.UserId)
            notifications.Add(uid, NotificationType.AttendanceUpdated, "Attendance record added",
                $"HR added an attendance record for {r.AttendanceDate:yyyy-MM-dd}. Reason: {r.Reason}",
                "تمت إضافة سجل حضور", $"أضافت الموارد البشرية سجل حضور ليوم {r.AttendanceDate:yyyy-MM-dd}. السبب: {r.Reason}",
                "/attendance/my", nameof(AttendanceRecord), record.Id);
        await notifications.SaveAndDispatchAsync(ct);
        return await db.AttendanceRecords.Where(a => a.Id == record.Id).Select(Projection).FirstAsync(ct);
    }

    // ------------------------------------------------------------------ QR kiosk

    public async Task<QrCodeDto> GetKioskQrAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetEntityAsync(ct);
        if (!settings.QrCodeEnabled) throw new BadRequestException("QR code check-in is disabled in system settings.");
        var token = verifier.CreateQrToken(settings, clock.UtcNow, out var expires);
        var baseUrl = (configuration["App:PublicUrl"] ?? "http://localhost:4200").TrimEnd('/');
        var url = $"{baseUrl}/attendance/scan?t={Uri.EscapeDataString(token)}";
        var png = Convert.ToBase64String(verifier.RenderQrPng(url));
        return new QrCodeDto(token, url, png, expires, settings.QrRotationSeconds);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Employee> RequireCurrentEmployeeAsync(CancellationToken ct)
    {
        var id = await scope.GetCurrentEmployeeIdAsync(ct) ?? throw new BadRequestException("Your account is not linked to an employee profile.");
        return await db.Employees.FirstAsync(e => e.Id == id, ct);
    }

    /// <summary>Latest record with a check-in and no check-out, from today or (for overnight shifts) yesterday.</summary>
    private Task<AttendanceRecord?> FindOpenRecordAsync(int employeeId, DateOnly today, CancellationToken ct)
    {
        var yesterday = today.AddDays(-1);
        return db.AttendanceRecords
            .Where(a => a.EmployeeId == employeeId && a.CheckInTime != null && a.CheckOutTime == null && !a.MissingCheckOut
                        && (a.AttendanceDate == today || a.AttendanceDate == yesterday))
            .OrderByDescending(a => a.AttendanceDate).ThenByDescending(a => a.ShiftNumber)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<VerificationMethod> VerifyOrThrowAsync(CompanySettings settings, Employee employee, CheckRequest r, string action,
        bool bypass, CancellationToken ct)
    {
        if (bypass || settings.EnabledMethods == VerificationMethod.None) return VerificationMethod.None;
        var result = verifier.Verify(settings, new VerificationInput(currentUser.IpAddress, r.Latitude, r.Longitude, r.Accuracy, r.QrToken));
        if (result.Success) return result.Passed;

        await audit.LogNowAsync($"{action}Rejected", nameof(AttendanceRecord), null,
            new { EmployeeId = employee.Id, currentUser.IpAddress, r.Latitude, r.Longitude, r.Accuracy, HasQrToken = !string.IsNullOrEmpty(r.QrToken) },
            result.FailureReason, ct);
        throw new BadRequestException(result.FailureReason ?? "On-site verification failed.");
    }

    /// <summary>
    /// Applies HR-entered times. Non-time statuses (Absent/OnLeave/Holiday) clear the times; WorkFromHome is kept through
    /// recalculation; any other explicit status overrides the computed one.
    /// </summary>
    private void ApplyTimesAndStatus(AttendanceRecord record, TimeOnly? checkIn, TimeOnly? checkOut, bool nextDay,
        AttendanceStatus? forced, WorkSchedule schedule, TimeZoneInfo tz)
    {
        if (forced is AttendanceStatus nonTime && AttendanceCalculator.NonTimeStatuses.Contains(nonTime))
        {
            record.CheckInTime = null;
            record.CheckOutTime = null;
            record.Status = nonTime;
            AttendanceCalculator.Recalculate(record, schedule);
            return;
        }

        record.CheckInTime = checkIn is TimeOnly cin ? clock.ToCompanyTime(record.AttendanceDate, cin, tz) : null;
        record.CheckOutTime = checkOut is TimeOnly cout
            ? clock.ToCompanyTime(nextDay ? record.AttendanceDate.AddDays(1) : record.AttendanceDate, cout, tz)
            : null;

        if (forced == AttendanceStatus.WorkFromHome) record.Status = AttendanceStatus.WorkFromHome;
        else if (record.Status == AttendanceStatus.WorkFromHome || AttendanceCalculator.NonTimeStatuses.Contains(record.Status))
            record.Status = AttendanceStatus.Present;

        AttendanceCalculator.Recalculate(record, schedule);
        if (forced is AttendanceStatus explicitStatus && explicitStatus != AttendanceStatus.WorkFromHome) record.Status = explicitStatus;
        if (record.CheckOutTime is not null) record.MissingCheckOut = false;
    }

    private static VerificationRequirementsDto BuildRequirements(CompanySettings s, Employee e) => new(
        s.EnabledMethods != VerificationMethod.None, s.VerificationPolicy, s.GeofenceEnabled, s.NetworkCheckEnabled, s.QrCodeEnabled,
        s.RequireVerificationOnCheckOut, e.AllowRemoteCheckIn);

    private static object Snapshot(AttendanceRecord a) => new
    {
        a.AttendanceDate, a.ShiftNumber, a.CheckInTime, a.CheckOutTime, a.WorkingHours, a.LateMinutes, a.EarlyLeaveMinutes,
        Status = a.Status.ToString(), a.Notes, a.MissingCheckOut
    };

    internal static Task<List<EmployeeSlim>> SlimAsync(IQueryable<Employee> q, CancellationToken ct) =>
        q.OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new EmployeeSlim(e.Id, e.EmployeeCode, e.FirstName + " " + e.LastName, e.DepartmentId, e.Department.Name,
                e.WorkScheduleId, e.HireDate, e.UserId, e.AllowRemoteCheckIn))
            .ToListAsync(ct);
}
