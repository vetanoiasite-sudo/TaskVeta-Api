using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Settings;

// ---------------- Work schedules ----------------

public record WorkScheduleDto(int Id, string Name, TimeOnly StartTime, TimeOnly EndTime, int LateGraceMinutes,
    int EarlyLeaveGraceMinutes, decimal HalfDayThresholdHours, DayOfWeek[] WeekendDays, bool IsDefault, int EmployeeCount);

public record SaveWorkScheduleRequest(string Name, TimeOnly StartTime, TimeOnly EndTime, int LateGraceMinutes,
    int EarlyLeaveGraceMinutes, decimal HalfDayThresholdHours, DayOfWeek[] WeekendDays, bool IsDefault);

public class SaveWorkScheduleRequestValidator : AbstractValidator<SaveWorkScheduleRequest>
{
    public SaveWorkScheduleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.EndTime).GreaterThan(x => x.StartTime).WithMessage("End time must be after start time.");
        RuleFor(x => x.LateGraceMinutes).InclusiveBetween(0, 240);
        RuleFor(x => x.EarlyLeaveGraceMinutes).InclusiveBetween(0, 240);
        RuleFor(x => x.HalfDayThresholdHours).InclusiveBetween(0, 24);
        RuleFor(x => x.WeekendDays).NotNull().Must(d => d.Length < 7).WithMessage("At least one working day is required.");
        RuleForEach(x => x.WeekendDays).IsInEnum();
    }
}

public interface IWorkScheduleService
{
    Task<IReadOnlyList<WorkScheduleDto>> GetAllAsync(CancellationToken ct = default);
    Task<WorkScheduleDto> CreateAsync(SaveWorkScheduleRequest r, CancellationToken ct = default);
    Task<WorkScheduleDto> UpdateAsync(int id, SaveWorkScheduleRequest r, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<WorkSchedule> GetDefaultAsync(CancellationToken ct = default);
}

public class WorkScheduleService(IApplicationDbContext db, IAuditService audit) : IWorkScheduleService
{
    public async Task<IReadOnlyList<WorkScheduleDto>> GetAllAsync(CancellationToken ct = default)
    {
        var list = await db.WorkSchedules.OrderByDescending(w => w.IsDefault).ThenBy(w => w.Name)
            .Select(w => new { W = w, Count = db.Employees.Count(e => e.WorkScheduleId == w.Id && !e.IsDeleted) })
            .ToListAsync(ct);
        return list.Select(x => ToDto(x.W, x.Count)).ToList();
    }

    public async Task<WorkScheduleDto> CreateAsync(SaveWorkScheduleRequest r, CancellationToken ct = default)
    {
        var w = new WorkSchedule();
        Apply(w, r);
        if (r.IsDefault || !await db.WorkSchedules.AnyAsync(ct)) await MakeDefaultAsync(w, ct);
        db.WorkSchedules.Add(w);
        await db.SaveChangesAsync(ct);
        audit.Log("Create", nameof(WorkSchedule), w.Id, newValues: r);
        await db.SaveChangesAsync(ct);
        return ToDto(w, 0);
    }

    public async Task<WorkScheduleDto> UpdateAsync(int id, SaveWorkScheduleRequest r, CancellationToken ct = default)
    {
        var w = await db.WorkSchedules.FindAsync([id], ct) ?? throw new NotFoundException(nameof(WorkSchedule), id);
        var old = ToDto(w, 0);
        var wasDefault = w.IsDefault;
        Apply(w, r);
        if (r.IsDefault && !wasDefault) await MakeDefaultAsync(w, ct);
        if (!r.IsDefault && wasDefault) w.IsDefault = true; // there must always be a default; change it by marking another
        audit.Log("Update", nameof(WorkSchedule), id, old, r);
        await db.SaveChangesAsync(ct);
        return ToDto(w, await db.Employees.CountAsync(e => e.WorkScheduleId == id && !e.IsDeleted, ct));
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var w = await db.WorkSchedules.FindAsync([id], ct) ?? throw new NotFoundException(nameof(WorkSchedule), id);
        if (w.IsDefault) throw new ConflictException("The default schedule cannot be deleted.");
        if (await db.Employees.AnyAsync(e => e.WorkScheduleId == id, ct))
            throw new ConflictException("Schedule is assigned to employees.");
        db.WorkSchedules.Remove(w);
        audit.Log("Delete", nameof(WorkSchedule), id, new { w.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task<WorkSchedule> GetDefaultAsync(CancellationToken ct = default) =>
        await db.WorkSchedules.AsNoTracking().OrderByDescending(w => w.IsDefault).ThenBy(w => w.Id).FirstOrDefaultAsync(ct)
        ?? new WorkSchedule { Name = "Standard", IsDefault = true };

    private async Task MakeDefaultAsync(WorkSchedule w, CancellationToken ct)
    {
        await db.WorkSchedules.Where(x => x.IsDefault && x.Id != w.Id).ForEachAsync(x => x.IsDefault = false, ct);
        w.IsDefault = true;
    }

    private static void Apply(WorkSchedule w, SaveWorkScheduleRequest r)
    {
        w.Name = r.Name.Trim();
        w.StartTime = r.StartTime;
        w.EndTime = r.EndTime;
        w.LateGraceMinutes = r.LateGraceMinutes;
        w.EarlyLeaveGraceMinutes = r.EarlyLeaveGraceMinutes;
        w.HalfDayThresholdHours = r.HalfDayThresholdHours;
        w.WeekendDaysMask = WorkSchedule.ToMask(r.WeekendDays);
    }

    private static WorkScheduleDto ToDto(WorkSchedule w, int count) => new(w.Id, w.Name, w.StartTime, w.EndTime, w.LateGraceMinutes,
        w.EarlyLeaveGraceMinutes, w.HalfDayThresholdHours, w.WeekendDays.ToArray(), w.IsDefault, count);
}

// ---------------- Holidays ----------------

public record HolidayDto(int Id, string Name, string? NameAr, DateOnly StartDate, DateOnly EndDate, string? Description);
public record SaveHolidayRequest(string Name, string? NameAr, DateOnly StartDate, DateOnly EndDate, string? Description);

public class SaveHolidayRequestValidator : AbstractValidator<SaveHolidayRequest>
{
    public SaveHolidayRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.NameAr).MaximumLength(150);
        RuleFor(x => x.EndDate).GreaterThanOrEqualTo(x => x.StartDate);
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public interface IHolidayService
{
    Task<IReadOnlyList<HolidayDto>> GetAsync(int? year, CancellationToken ct = default);
    Task<HolidayDto> CreateAsync(SaveHolidayRequest r, CancellationToken ct = default);
    Task<HolidayDto> UpdateAsync(int id, SaveHolidayRequest r, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public class HolidayService(IApplicationDbContext db, IAuditService audit) : IHolidayService
{
    public async Task<IReadOnlyList<HolidayDto>> GetAsync(int? year, CancellationToken ct = default)
    {
        var q = db.Holidays.AsQueryable();
        if (year is int y) q = q.Where(h => h.StartDate.Year == y || h.EndDate.Year == y);
        return await q.OrderBy(h => h.StartDate)
            .Select(h => new HolidayDto(h.Id, h.Name, h.NameAr, h.StartDate, h.EndDate, h.Description)).ToListAsync(ct);
    }

    public async Task<HolidayDto> CreateAsync(SaveHolidayRequest r, CancellationToken ct = default)
    {
        var h = new Holiday { Name = r.Name.Trim(), NameAr = r.NameAr, StartDate = r.StartDate, EndDate = r.EndDate, Description = r.Description };
        db.Holidays.Add(h);
        await db.SaveChangesAsync(ct);
        audit.Log("Create", nameof(Holiday), h.Id, newValues: r);
        await db.SaveChangesAsync(ct);
        return new HolidayDto(h.Id, h.Name, h.NameAr, h.StartDate, h.EndDate, h.Description);
    }

    public async Task<HolidayDto> UpdateAsync(int id, SaveHolidayRequest r, CancellationToken ct = default)
    {
        var h = await db.Holidays.FindAsync([id], ct) ?? throw new NotFoundException(nameof(Holiday), id);
        var old = new { h.Name, h.StartDate, h.EndDate };
        h.Name = r.Name.Trim(); h.NameAr = r.NameAr; h.StartDate = r.StartDate; h.EndDate = r.EndDate; h.Description = r.Description;
        audit.Log("Update", nameof(Holiday), id, old, r);
        await db.SaveChangesAsync(ct);
        return new HolidayDto(h.Id, h.Name, h.NameAr, h.StartDate, h.EndDate, h.Description);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var h = await db.Holidays.FindAsync([id], ct) ?? throw new NotFoundException(nameof(Holiday), id);
        db.Holidays.Remove(h);
        audit.Log("Delete", nameof(Holiday), id, new { h.Name, h.StartDate, h.EndDate });
        await db.SaveChangesAsync(ct);
    }
}

// ---------------- Leave requests ----------------

public record LeaveRequestDto(int Id, int EmployeeId, string EmployeeName, string EmployeeCode, string DepartmentName,
    LeaveType Type, DateOnly StartDate, DateOnly EndDate, int Days, string? Reason, LeaveStatus Status,
    string? ReviewedBy, DateTime? ReviewedAt, string? ReviewNotes, DateTime CreatedAt);

public class LeaveQuery : PagedRequest
{
    public int? EmployeeId { get; set; }
    public int? DepartmentId { get; set; }
    public LeaveStatus? Status { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public bool Mine { get; set; }
}

public record CreateLeaveRequest(int? EmployeeId, LeaveType Type, DateOnly StartDate, DateOnly EndDate, string? Reason);
public record ReviewLeaveRequest(bool Approve, string? Notes);

public class CreateLeaveRequestValidator : AbstractValidator<CreateLeaveRequest>
{
    public CreateLeaveRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.EndDate).GreaterThanOrEqualTo(x => x.StartDate);
        RuleFor(x => x).Must(x => x.EndDate.DayNumber - x.StartDate.DayNumber <= 90).WithMessage("Leave cannot exceed 90 days.");
        RuleFor(x => x.Reason).MaximumLength(1000);
    }
}

public class ReviewLeaveRequestValidator : AbstractValidator<ReviewLeaveRequest>
{
    public ReviewLeaveRequestValidator() => RuleFor(x => x.Notes).MaximumLength(1000);
}

public interface ILeaveService
{
    Task<PagedResult<LeaveRequestDto>> GetPagedAsync(LeaveQuery q, CancellationToken ct = default);
    Task<LeaveRequestDto> CreateAsync(CreateLeaveRequest r, CancellationToken ct = default);
    Task<LeaveRequestDto> ReviewAsync(int id, ReviewLeaveRequest r, CancellationToken ct = default);
    Task CancelAsync(int id, CancellationToken ct = default);
}

public class LeaveService(
    IApplicationDbContext db, IAccessScope scope, ICurrentUser currentUser, IAuditService audit,
    INotificationService notifications, ICompanyClock clock) : ILeaveService
{
    private IQueryable<LeaveRequestDto> Project(IQueryable<LeaveRequest> q) => q.Select(l => new LeaveRequestDto(
        l.Id, l.EmployeeId, l.Employee.FirstName + " " + l.Employee.LastName, l.Employee.EmployeeCode, l.Employee.Department.Name,
        l.Type, l.StartDate, l.EndDate, l.EndDate.DayNumber - l.StartDate.DayNumber + 1, l.Reason, l.Status,
        db.Users.Where(u => u.Id == l.ReviewedByUserId).Select(u => u.FullName).FirstOrDefault(), l.ReviewedAt, l.ReviewNotes, l.CreatedAt));

    public async Task<PagedResult<LeaveRequestDto>> GetPagedAsync(LeaveQuery q, CancellationToken ct = default)
    {
        var employees = await scope.ScopedEmployeesAsync(ct);
        var query = db.LeaveRequests.Where(l => employees.Any(e => e.Id == l.EmployeeId));
        if (q.Mine)
        {
            var me = await scope.GetCurrentEmployeeIdAsync(ct) ?? -1;
            query = query.Where(l => l.EmployeeId == me);
        }
        if (q.EmployeeId is int emp) query = query.Where(l => l.EmployeeId == emp);
        if (q.DepartmentId is int d) query = query.Where(l => l.Employee.DepartmentId == d);
        if (q.Status is LeaveStatus s) query = query.Where(l => l.Status == s);
        if (q.From is DateOnly f) query = query.Where(l => l.EndDate >= f);
        if (q.To is DateOnly t) query = query.Where(l => l.StartDate <= t);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim().ToLower();
            query = query.Where(l => (l.Employee.FirstName + " " + l.Employee.LastName).ToLower().Contains(term) || l.Employee.EmployeeCode.ToLower().Contains(term));
        }
        return await Project(query.OrderByDescending(l => l.CreatedAt)).ToPagedAsync(q, ct);
    }

    public async Task<LeaveRequestDto> CreateAsync(CreateLeaveRequest r, CancellationToken ct = default)
    {
        var me = await scope.GetCurrentEmployeeIdAsync(ct);
        var employeeId = r.EmployeeId ?? me ?? throw new BadRequestException("Your account is not linked to an employee.");
        if (employeeId != me && !currentUser.HasPermission(Permissions.LeaveApprove))
            throw new ForbiddenException("You can only request leave for yourself.");
        await scope.EnsureCanAccessEmployeeAsync(employeeId, ct);

        var overlap = await db.LeaveRequests.AnyAsync(l => l.EmployeeId == employeeId
            && (l.Status == LeaveStatus.Pending || l.Status == LeaveStatus.Approved)
            && l.StartDate <= r.EndDate && l.EndDate >= r.StartDate, ct);
        if (overlap) throw new ConflictException("An overlapping leave request already exists.");

        var leave = new LeaveRequest { EmployeeId = employeeId, Type = r.Type, StartDate = r.StartDate, EndDate = r.EndDate, Reason = r.Reason };
        db.LeaveRequests.Add(leave);
        await db.SaveChangesAsync(ct);
        audit.Log("Create", nameof(LeaveRequest), leave.Id, newValues: r);

        // Notify the employee's managers (department manager + team leaders).
        var managers = await ManagerLookup.GetManagerEmployeeIdsAsync(db, employeeId, ct);
        var name = await db.Employees.Where(e => e.Id == employeeId).Select(e => e.FirstName + " " + e.LastName).FirstAsync(ct);
        notifications.AddForEmployees(managers, NotificationType.LeaveRequest, "New leave request",
            $"{name} requested {r.Type} leave from {r.StartDate:yyyy-MM-dd} to {r.EndDate:yyyy-MM-dd}.",
            "طلب إجازة جديد", $"قدّم {name} طلب إجازة من {r.StartDate:yyyy-MM-dd} إلى {r.EndDate:yyyy-MM-dd}.",
            "/leave", nameof(LeaveRequest), leave.Id);
        await notifications.SaveAndDispatchAsync(ct);
        return await Project(db.LeaveRequests.Where(l => l.Id == leave.Id)).FirstAsync(ct);
    }

    public async Task<LeaveRequestDto> ReviewAsync(int id, ReviewLeaveRequest r, CancellationToken ct = default)
    {
        var leave = await db.LeaveRequests.Include(l => l.Employee).FirstOrDefaultAsync(l => l.Id == id, ct)
                    ?? throw new NotFoundException(nameof(LeaveRequest), id);
        await scope.EnsureCanAccessEmployeeAsync(leave.EmployeeId, ct);
        if (leave.EmployeeId == await scope.GetCurrentEmployeeIdAsync(ct) && !currentUser.HasPermission(Permissions.AttendanceViewAll))
            throw new ForbiddenException("You cannot approve your own leave.");
        if (leave.Status != LeaveStatus.Pending) throw new ConflictException("Only pending requests can be reviewed.");

        leave.Status = r.Approve ? LeaveStatus.Approved : LeaveStatus.Rejected;
        leave.ReviewedByUserId = currentUser.UserId;
        leave.ReviewedAt = clock.UtcNow;
        leave.ReviewNotes = r.Notes;

        if (r.Approve)
        {
            // Convert any already-materialized Absent records inside the leave period to OnLeave.
            var absents = await db.AttendanceRecords.Where(a => a.EmployeeId == leave.EmployeeId
                && a.AttendanceDate >= leave.StartDate && a.AttendanceDate <= leave.EndDate
                && a.Status == AttendanceStatus.Absent && a.CheckInTime == null).ToListAsync(ct);
            foreach (var a in absents) { a.Status = AttendanceStatus.OnLeave; a.Notes = $"Approved leave #{leave.Id}"; }
        }

        audit.Log(r.Approve ? "Approve" : "Reject", nameof(LeaveRequest), id, reason: r.Notes);
        if (leave.Employee.UserId is int uid)
            notifications.Add(uid, NotificationType.LeaveRequest,
                r.Approve ? "Leave approved" : "Leave rejected",
                $"Your leave request ({leave.StartDate:yyyy-MM-dd} – {leave.EndDate:yyyy-MM-dd}) was {(r.Approve ? "approved" : "rejected")}.",
                r.Approve ? "تمت الموافقة على الإجازة" : "تم رفض الإجازة",
                $"طلب إجازتك ({leave.StartDate:yyyy-MM-dd} – {leave.EndDate:yyyy-MM-dd}) {(r.Approve ? "تمت الموافقة عليه" : "تم رفضه")}.",
                "/leave", nameof(LeaveRequest), id);
        await notifications.SaveAndDispatchAsync(ct);
        return await Project(db.LeaveRequests.Where(l => l.Id == id)).FirstAsync(ct);
    }

    public async Task CancelAsync(int id, CancellationToken ct = default)
    {
        var leave = await db.LeaveRequests.FindAsync([id], ct) ?? throw new NotFoundException(nameof(LeaveRequest), id);
        var me = await scope.GetCurrentEmployeeIdAsync(ct);
        if (leave.EmployeeId != me && !currentUser.HasPermission(Permissions.AttendanceViewAll))
            throw new ForbiddenException();
        if (leave.Status is not (LeaveStatus.Pending or LeaveStatus.Approved)) throw new ConflictException("Request cannot be cancelled.");
        if (leave.Status == LeaveStatus.Approved && leave.StartDate <= await clock.TodayAsync(ct) && !currentUser.HasPermission(Permissions.AttendanceViewAll))
            throw new ConflictException("Leave that already started can only be cancelled by HR.");
        leave.Status = LeaveStatus.Cancelled;
        audit.Log("Cancel", nameof(LeaveRequest), id);
        await db.SaveChangesAsync(ct);
    }
}
