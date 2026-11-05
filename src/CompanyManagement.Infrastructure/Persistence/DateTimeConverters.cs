using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CompanyManagement.Infrastructure.Persistence;

/// <summary>
/// The company time zone used to turn stored UTC instants back into company wall-clock times.
/// Kept current by <see cref="Services.CompanyClock"/> whenever it resolves the zone from settings.
/// </summary>
public static class CompanyTimeZone
{
    private static volatile TimeZoneInfo _current = TimeZoneInfo.Local;

    public static TimeZoneInfo Current
    {
        get => _current;
        set => _current = value ?? TimeZoneInfo.Local;
    }

    public static DateTimeOffset FromUtc(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _current);
}

/// <summary>
/// PostgreSQL <c>timestamptz</c> only accepts UTC. Attendance times are written as UTC and read back in the
/// company time zone, so the rest of the app keeps seeing the same wall-clock offsets as before.
/// </summary>
public class CompanyDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    v => v.ToUniversalTime(),
    v => CompanyTimeZone.FromUtc(v));

/// <summary>All <see cref="DateTime"/> columns are UTC; normalizes values whose Kind was lost (e.g. from JSON or tests).</summary>
public class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
