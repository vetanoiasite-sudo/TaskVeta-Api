using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Settings;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using CompanyManagement.Domain.Services;
using CompanyManagement.Infrastructure.Services;
using Microsoft.Extensions.Time.Testing;

namespace CompanyManagement.Tests;

public class AttendanceCalculatorTests
{
    private static readonly WorkSchedule Schedule = new()
    {
        StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0), LateGraceMinutes = 15, EarlyLeaveGraceMinutes = 10,
        HalfDayThresholdHours = 4
    };

    private static AttendanceRecord Record(int inH, int inM, int? outH = null, int? outM = null, AttendanceStatus status = AttendanceStatus.Present)
    {
        var offset = TimeSpan.FromHours(3);
        return new AttendanceRecord
        {
            AttendanceDate = new DateOnly(2026, 3, 2),
            Status = status,
            CheckInTime = new DateTimeOffset(2026, 3, 2, inH, inM, 0, offset),
            CheckOutTime = outH is null ? null : new DateTimeOffset(2026, 3, 2, outH.Value, outM ?? 0, 0, offset)
        };
    }

    [Theory]
    [InlineData(8, 30, 0)]
    [InlineData(9, 0, 0)]
    [InlineData(9, 15, 0)]   // within grace
    [InlineData(9, 16, 16)]  // beyond grace: counted from start time
    [InlineData(10, 30, 90)]
    public void Late_minutes_respect_grace_period(int h, int m, int expected) =>
        Assert.Equal(expected, AttendanceCalculator.CalculateLateMinutes(new TimeOnly(h, m), Schedule));

    [Theory]
    [InlineData(17, 0, 0)]
    [InlineData(16, 50, 0)]  // within grace
    [InlineData(16, 30, 30)]
    [InlineData(18, 0, 0)]
    public void Early_leave_minutes_respect_grace_period(int h, int m, int expected) =>
        Assert.Equal(expected, AttendanceCalculator.CalculateEarlyLeaveMinutes(new TimeOnly(h, m), Schedule));

    [Fact]
    public void On_time_full_day_is_present_with_working_hours()
    {
        var r = Record(8, 55, 17, 5);
        AttendanceCalculator.Recalculate(r, Schedule);
        Assert.Equal(AttendanceStatus.Present, r.Status);
        Assert.Equal(8.17m, r.WorkingHours);
        Assert.Equal(0, r.LateMinutes);
    }

    [Fact]
    public void Late_arrival_is_late()
    {
        var r = Record(9, 40, 17, 30);
        AttendanceCalculator.Recalculate(r, Schedule);
        Assert.Equal(AttendanceStatus.Late, r.Status);
        Assert.Equal(40, r.LateMinutes);
    }

    [Fact]
    public void Leaving_early_is_early_leave()
    {
        var r = Record(9, 0, 15, 0);
        AttendanceCalculator.Recalculate(r, Schedule);
        Assert.Equal(AttendanceStatus.EarlyLeave, r.Status);
        Assert.Equal(120, r.EarlyLeaveMinutes);
    }

    [Fact]
    public void Short_day_is_half_day()
    {
        var r = Record(9, 0, 12, 0);
        AttendanceCalculator.Recalculate(r, Schedule);
        Assert.Equal(AttendanceStatus.HalfDay, r.Status);
        Assert.Equal(3m, r.WorkingHours);
    }

    [Fact]
    public void Open_record_is_present_or_late_without_hours()
    {
        var r = Record(9, 30);
        AttendanceCalculator.Recalculate(r, Schedule);
        Assert.Equal(AttendanceStatus.Late, r.Status);
        Assert.Equal(0m, r.WorkingHours);
    }

    [Fact]
    public void Work_from_home_status_is_preserved()
    {
        var r = Record(10, 0, 16, 0, AttendanceStatus.WorkFromHome);
        AttendanceCalculator.Recalculate(r, Schedule);
        Assert.Equal(AttendanceStatus.WorkFromHome, r.Status);
        Assert.Equal(60, r.LateMinutes);
    }

    [Fact]
    public void Record_without_check_in_keeps_on_leave_status()
    {
        var r = new AttendanceRecord { Status = AttendanceStatus.OnLeave };
        AttendanceCalculator.Recalculate(r, Schedule);
        Assert.Equal(AttendanceStatus.OnLeave, r.Status);
    }

    [Fact]
    public void Weekends_and_holidays_are_not_working_days()
    {
        var schedule = new WorkSchedule { WeekendDaysMask = WorkSchedule.ToMask([DayOfWeek.Friday, DayOfWeek.Saturday]) };
        var holiday = new Holiday { StartDate = new DateOnly(2026, 3, 3), EndDate = new DateOnly(2026, 3, 4) };
        Assert.True(AttendanceCalculator.IsWorkingDay(new DateOnly(2026, 3, 2), schedule, [holiday]));   // Monday
        Assert.False(AttendanceCalculator.IsWorkingDay(new DateOnly(2026, 3, 4), schedule, [holiday]));  // holiday
        Assert.False(AttendanceCalculator.IsWorkingDay(new DateOnly(2026, 3, 6), schedule, [holiday]));  // Friday
    }
}

public class TaskWorkflowTests
{
    [Theory]
    [InlineData(TaskItemStatus.Assigned, TaskItemStatus.InProgress, false, true, true)]
    [InlineData(TaskItemStatus.InProgress, TaskItemStatus.PendingReview, false, true, true)]
    [InlineData(TaskItemStatus.InProgress, TaskItemStatus.Completed, false, true, false)]      // employees cannot self-approve
    [InlineData(TaskItemStatus.PendingReview, TaskItemStatus.Completed, true, false, true)]
    [InlineData(TaskItemStatus.PendingReview, TaskItemStatus.Rejected, true, false, true)]
    [InlineData(TaskItemStatus.PendingReview, TaskItemStatus.Completed, false, true, false)]
    [InlineData(TaskItemStatus.Completed, TaskItemStatus.Cancelled, true, false, false)]
    [InlineData(TaskItemStatus.Rejected, TaskItemStatus.InProgress, false, true, true)]
    public void Transitions_follow_workflow(TaskItemStatus from, TaskItemStatus to, bool reviewer, bool assignee, bool allowed) =>
        Assert.Equal(allowed, TaskWorkflow.CanTransition(from, to, reviewer, assignee));
}

public class PermissionTests
{
    [Fact]
    public void Employees_only_have_self_service() =>
        Assert.Equal([Permissions.AttendanceSelf], Permissions.ForRoles([Roles.Employee]));

    [Fact]
    public void Managers_cannot_view_all_attendance_or_correct_it()
    {
        var p = Permissions.ForRoles([Roles.Manager]).ToList();
        Assert.Contains(Permissions.AttendanceViewTeam, p);
        Assert.DoesNotContain(Permissions.AttendanceViewAll, p);
        Assert.DoesNotContain(Permissions.AttendanceCorrect, p);
    }

    [Fact]
    public void Only_super_admin_manages_users_and_settings()
    {
        Assert.DoesNotContain(Permissions.UsersManage, Permissions.ForRoles([Roles.Admin]));
        Assert.DoesNotContain(Permissions.SettingsManage, Permissions.ForRoles([Roles.Admin]));
        Assert.Contains(Permissions.UsersManage, Permissions.ForRoles([Roles.SuperAdmin]));
    }

    [Fact]
    public void Department_and_team_leads_can_assign_and_review_tasks_only()
    {
        var p = Permissions.ForRoles([Roles.Employee, Roles.Leader]).ToList();
        Assert.Contains(Permissions.TasksAssign, p);
        Assert.Contains(Permissions.TasksReview, p);
        Assert.DoesNotContain(Permissions.TasksViewAll, p);
        Assert.DoesNotContain(Permissions.AttendanceViewTeam, p);
        Assert.DoesNotContain(Roles.Leader, Roles.All); // never assignable from the Users page
    }
}

public class AttendanceVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 6, 0, 0, TimeSpan.Zero);

    private static (AttendanceVerifier Verifier, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(Now);
        return (new AttendanceVerifier(time), time);
    }

    private static CompanySettings Settings(Action<CompanySettings>? configure = null)
    {
        var s = new CompanySettings
        {
            QrSecret = SettingsService.NewSecret(), QrRotationSeconds = 30,
            OfficeLatitude = 24.7136, OfficeLongitude = 46.6753, GeofenceRadiusMeters = 150, MaxLocationAccuracyMeters = 100,
            AllowedIpRanges = "10.20.0.0/16, 192.168.1.50"
        };
        configure?.Invoke(s);
        return s;
    }

    [Fact]
    public void No_methods_enabled_always_passes()
    {
        var (v, _) = Create();
        Assert.True(v.Verify(Settings(), new VerificationInput(null, null, null, null, null)).Success);
    }

    [Fact]
    public void Geofence_accepts_inside_and_rejects_outside_radius()
    {
        var (v, _) = Create();
        var s = Settings(x => x.GeofenceEnabled = true);
        Assert.True(v.Verify(s, new VerificationInput(null, 24.7140, 46.6756, 10, null)).Success);
        var far = v.Verify(s, new VerificationInput(null, 24.7236, 46.6753, 10, null));
        Assert.False(far.Success);
        Assert.Contains("m from the office", far.FailureReason);
    }

    [Fact]
    public void Geofence_rejects_imprecise_location()
    {
        var (v, _) = Create();
        var s = Settings(x => x.GeofenceEnabled = true);
        Assert.False(v.Verify(s, new VerificationInput(null, 24.7136, 46.6753, 500, null)).Success);
    }

    [Theory]
    [InlineData("10.20.5.9", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("192.168.1.51", false)]
    [InlineData("::ffff:10.20.0.1", true)]
    [InlineData("8.8.8.8", false)]
    public void Network_check_uses_cidr_allow_list(string ip, bool expected)
    {
        var (v, _) = Create();
        var s = Settings(x => x.NetworkCheckEnabled = true);
        Assert.Equal(expected, v.Verify(s, new VerificationInput(ip, null, null, null, null)).Success);
    }

    [Fact]
    public void Qr_token_is_valid_for_current_and_previous_window_only()
    {
        var (v, time) = Create();
        var s = Settings(x => x.QrCodeEnabled = true);
        var token = v.CreateQrToken(s, Now.UtcDateTime, out _);
        Assert.True(v.ValidateQrToken(s, token, Now.UtcDateTime));
        Assert.True(v.ValidateQrToken(s, token, Now.UtcDateTime.AddSeconds(30)));
        Assert.False(v.ValidateQrToken(s, token, Now.UtcDateTime.AddSeconds(65)));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(v.Verify(s, new VerificationInput(null, null, null, null, token)).Success);
    }

    [Fact]
    public void Qr_token_with_wrong_signature_or_secret_is_rejected()
    {
        var (v, _) = Create();
        var s = Settings(x => x.QrCodeEnabled = true);
        var token = v.CreateQrToken(s, Now.UtcDateTime, out _);
        Assert.False(v.ValidateQrToken(s, token[..^1] + (token[^1] == 'A' ? 'B' : 'A'), Now.UtcDateTime));
        var rotated = Settings(x => x.QrCodeEnabled = true);
        Assert.False(v.ValidateQrToken(rotated, token, Now.UtcDateTime));
    }

    [Fact]
    public void Policy_any_vs_all()
    {
        var (v, _) = Create();
        var input = new VerificationInput("10.20.0.1", 24.7236, 46.6753, 10, null); // network OK, location outside
        var any = Settings(x => { x.GeofenceEnabled = true; x.NetworkCheckEnabled = true; x.VerificationPolicy = VerificationPolicy.Any; });
        var all = Settings(x => { x.GeofenceEnabled = true; x.NetworkCheckEnabled = true; x.VerificationPolicy = VerificationPolicy.All; });
        var r1 = v.Verify(any, input);
        Assert.True(r1.Success);
        Assert.Equal(VerificationMethod.Network, r1.Passed);
        Assert.False(v.Verify(all, input).Success);
    }

    [Fact]
    public void Ip_range_parser_rejects_garbage()
    {
        Assert.True(IpRangeParser.IsValid("10.0.0.0/8\n192.168.0.1, 2001:db8::/32"));
        Assert.False(IpRangeParser.IsValid("10.0.0.999/8"));
        Assert.False(IpRangeParser.IsValid("office-network"));
    }

    [Fact]
    public void Haversine_distance_is_accurate()
    {
        // ~0.01 degrees of latitude ≈ 1112 m
        var d = AttendanceVerifier.HaversineMeters(24.7136, 46.6753, 24.7236, 46.6753);
        Assert.InRange(d, 1100, 1125);
    }
}

public class QueryHelperTests
{
    [Fact]
    public void Page_size_is_clamped()
    {
        var r = new PagedRequest { PageSize = 10_000 };
        Assert.Equal(200, r.PageSize);
    }

    [Fact]
    public void In_memory_paging_returns_total_count()
    {
        var result = Enumerable.Range(1, 45).ToPaged(new PagedRequest { Page = 3, PageSize = 20 });
        Assert.Equal(45, result.TotalCount);
        Assert.Equal(5, result.Items.Count);
        Assert.Equal(3, result.TotalPages);
    }
}
