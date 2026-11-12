using System.Net;
using System.Security.Cryptography;
using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Settings;

public record SettingsDto(
    string CompanyName, string TimeZoneId, bool AllowMultipleShifts, int MaxShiftsPerDay, int TaskDeadlineReminderHours,
    int AttendanceFinalizeDelayMinutes, VerificationPolicy VerificationPolicy, bool RequireVerificationOnCheckOut,
    bool GeofenceEnabled, double? OfficeLatitude, double? OfficeLongitude, int GeofenceRadiusMeters, int MaxLocationAccuracyMeters,
    bool NetworkCheckEnabled, string? AllowedIpRanges, bool QrCodeEnabled, int QrRotationSeconds, DateTime UpdatedAt,
    int SprintLengthDays, CapacityTargetDto? CapacityTarget);

/// <summary>How the company wants sprint capacity split (% of estimated days): new features, improvements (incl. tech debt), bugs.</summary>
public record CapacityTargetDto(int New, int Improve, int Bugs)
{
    public static CapacityTargetDto? From(CompanySettings s) =>
        s.CapacityNewPercent is int n && s.CapacityImprovePercent is int i && s.CapacityBugsPercent is int b ? new(n, i, b) : null;
}

/// <summary>Subset of settings every authenticated user needs (e.g. to know which verification to perform).</summary>
public record PublicSettingsDto(string CompanyName, string TimeZoneId, bool AllowMultipleShifts, VerificationPolicy VerificationPolicy,
    bool RequireVerificationOnCheckOut, bool GeofenceEnabled, bool NetworkCheckEnabled, bool QrCodeEnabled, int SprintLengthDays = 7);

public record UpdateSettingsRequest(
    string CompanyName, string TimeZoneId, bool AllowMultipleShifts, int MaxShiftsPerDay, int TaskDeadlineReminderHours,
    int AttendanceFinalizeDelayMinutes, VerificationPolicy VerificationPolicy, bool RequireVerificationOnCheckOut,
    bool GeofenceEnabled, double? OfficeLatitude, double? OfficeLongitude, int GeofenceRadiusMeters, int MaxLocationAccuracyMeters,
    bool NetworkCheckEnabled, string? AllowedIpRanges, bool QrCodeEnabled, int QrRotationSeconds,
    int SprintLengthDays = 7, CapacityTargetDto? CapacityTarget = null);

public record TimeZoneDto(string Id, string DisplayName);

public static class IpRangeParser
{
    public static IReadOnlyList<IPNetwork> Parse(string? ranges)
    {
        var list = new List<IPNetwork>();
        if (string.IsNullOrWhiteSpace(ranges)) return list;
        foreach (var raw in ranges.Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseOne(raw.Trim(), out var net)) list.Add(net);
            else throw new FormatException($"'{raw}' is not a valid IP address or CIDR range.");
        }
        return list;
    }

    public static bool TryParseOne(string value, out IPNetwork network)
    {
        if (IPNetwork.TryParse(value, out network)) return true;
        if (IPAddress.TryParse(value, out var ip))
        {
            network = new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }
        return false;
    }

    public static bool IsValid(string? ranges)
    {
        try { Parse(ranges); return true; } catch (FormatException) { return false; }
    }
}

public class UpdateSettingsRequestValidator : AbstractValidator<UpdateSettingsRequest>
{
    public UpdateSettingsRequestValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.TimeZoneId).NotEmpty().Must(id => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _)).WithMessage("Unknown time zone.");
        RuleFor(x => x.MaxShiftsPerDay).InclusiveBetween(1, 5);
        RuleFor(x => x.TaskDeadlineReminderHours).InclusiveBetween(1, 24 * 14);
        RuleFor(x => x.AttendanceFinalizeDelayMinutes).InclusiveBetween(0, 24 * 60);
        RuleFor(x => x.VerificationPolicy).IsInEnum();
        RuleFor(x => x.GeofenceRadiusMeters).InclusiveBetween(10, 10000);
        RuleFor(x => x.MaxLocationAccuracyMeters).InclusiveBetween(5, 5000);
        RuleFor(x => x.QrRotationSeconds).InclusiveBetween(10, 600);
        RuleFor(x => x.SprintLengthDays).InclusiveBetween(1, 30);
        When(x => x.CapacityTarget is not null, () =>
        {
            RuleFor(x => x.CapacityTarget!.New).InclusiveBetween(0, 100).OverridePropertyName("CapacityTarget");
            RuleFor(x => x.CapacityTarget!.Improve).InclusiveBetween(0, 100).OverridePropertyName("CapacityTarget");
            RuleFor(x => x.CapacityTarget!.Bugs).InclusiveBetween(0, 100).OverridePropertyName("CapacityTarget");
            RuleFor(x => x.CapacityTarget).Must(t => t!.New + t.Improve + t.Bugs == 100)
                .WithMessage("The capacity target must add up to 100%.");
        });
        When(x => x.GeofenceEnabled, () =>
        {
            RuleFor(x => x.OfficeLatitude).NotNull().InclusiveBetween(-90, 90);
            RuleFor(x => x.OfficeLongitude).NotNull().InclusiveBetween(-180, 180);
        });
        When(x => x.NetworkCheckEnabled, () =>
            RuleFor(x => x.AllowedIpRanges).NotEmpty().WithMessage("Provide at least one allowed IP / CIDR range."));
        RuleFor(x => x.AllowedIpRanges).MaximumLength(4000).Must(IpRangeParser.IsValid).WithMessage("Invalid IP address or CIDR range.");
    }
}

public interface ISettingsService
{
    Task<CompanySettings> GetEntityAsync(CancellationToken ct = default);
    Task<SettingsDto> GetAsync(CancellationToken ct = default);
    Task<PublicSettingsDto> GetPublicAsync(CancellationToken ct = default);
    Task<SettingsDto> UpdateAsync(UpdateSettingsRequest request, CancellationToken ct = default);
    Task RegenerateQrSecretAsync(CancellationToken ct = default);
    IReadOnlyList<TimeZoneDto> GetTimeZones();
}

public class SettingsService(IApplicationDbContext db, IAuditService audit, ICompanyClock clock) : ISettingsService
{
    public async Task<CompanySettings> GetEntityAsync(CancellationToken ct = default)
    {
        var s = await db.CompanySettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is not null) return s;
        s = new CompanySettings { TimeZoneId = TimeZoneInfo.Local.Id, QrSecret = NewSecret() };
        db.CompanySettings.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    public async Task<SettingsDto> GetAsync(CancellationToken ct = default) => ToDto(await GetEntityAsync(ct));

    public async Task<PublicSettingsDto> GetPublicAsync(CancellationToken ct = default)
    {
        var s = await GetEntityAsync(ct);
        return new PublicSettingsDto(s.CompanyName, s.TimeZoneId, s.AllowMultipleShifts, s.VerificationPolicy,
            s.RequireVerificationOnCheckOut, s.GeofenceEnabled, s.NetworkCheckEnabled, s.QrCodeEnabled, s.SprintLengthDays);
    }

    public async Task<SettingsDto> UpdateAsync(UpdateSettingsRequest r, CancellationToken ct = default)
    {
        var s = await GetEntityAsync(ct);
        var old = ToDto(s);
        s.CompanyName = r.CompanyName.Trim();
        s.TimeZoneId = r.TimeZoneId;
        s.AllowMultipleShifts = r.AllowMultipleShifts;
        s.MaxShiftsPerDay = r.AllowMultipleShifts ? r.MaxShiftsPerDay : 1;
        s.TaskDeadlineReminderHours = r.TaskDeadlineReminderHours;
        s.AttendanceFinalizeDelayMinutes = r.AttendanceFinalizeDelayMinutes;
        s.VerificationPolicy = r.VerificationPolicy;
        s.RequireVerificationOnCheckOut = r.RequireVerificationOnCheckOut;
        s.GeofenceEnabled = r.GeofenceEnabled;
        s.OfficeLatitude = r.OfficeLatitude;
        s.OfficeLongitude = r.OfficeLongitude;
        s.GeofenceRadiusMeters = r.GeofenceRadiusMeters;
        s.MaxLocationAccuracyMeters = r.MaxLocationAccuracyMeters;
        s.NetworkCheckEnabled = r.NetworkCheckEnabled;
        s.AllowedIpRanges = r.AllowedIpRanges?.Trim();
        s.QrCodeEnabled = r.QrCodeEnabled;
        s.QrRotationSeconds = r.QrRotationSeconds;
        s.SprintLengthDays = r.SprintLengthDays;
        s.CapacityNewPercent = r.CapacityTarget?.New;
        s.CapacityImprovePercent = r.CapacityTarget?.Improve;
        s.CapacityBugsPercent = r.CapacityTarget?.Bugs;
        if (string.IsNullOrEmpty(s.QrSecret)) s.QrSecret = NewSecret();
        audit.Log("Update", "SystemSettings", s.Id, old, r);
        await db.SaveChangesAsync(ct);
        clock.InvalidateCache();
        await clock.GetTimeZoneAsync(ct); // reload so database reads use the new time zone right away
        return ToDto(s);
    }

    public async Task RegenerateQrSecretAsync(CancellationToken ct = default)
    {
        var s = await GetEntityAsync(ct);
        s.QrSecret = NewSecret();
        audit.Log("RegenerateQrSecret", "SystemSettings", s.Id);
        await db.SaveChangesAsync(ct);
    }

    public IReadOnlyList<TimeZoneDto> GetTimeZones() =>
        TimeZoneInfo.GetSystemTimeZones().Select(t => new TimeZoneDto(t.Id, t.DisplayName)).ToList();

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static SettingsDto ToDto(CompanySettings s) => new(
        s.CompanyName, s.TimeZoneId, s.AllowMultipleShifts, s.MaxShiftsPerDay, s.TaskDeadlineReminderHours,
        s.AttendanceFinalizeDelayMinutes, s.VerificationPolicy, s.RequireVerificationOnCheckOut, s.GeofenceEnabled,
        s.OfficeLatitude, s.OfficeLongitude, s.GeofenceRadiusMeters, s.MaxLocationAccuracyMeters, s.NetworkCheckEnabled,
        s.AllowedIpRanges, s.QrCodeEnabled, s.QrRotationSeconds, s.UpdatedAt, s.SprintLengthDays, CapacityTargetDto.From(s));
}
