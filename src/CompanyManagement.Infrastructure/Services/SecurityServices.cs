using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Settings;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QRCoder;

namespace CompanyManagement.Infrastructure.Services;

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "CompanyManagement";
    public string Audience { get; set; } = "CompanyManagement.Client";
    /// <summary>HMAC-SHA256 key, at least 32 characters. Supply via user-secrets or environment variables.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}

public class TokenService(IOptions<JwtOptions> options, TimeProvider time) : ITokenService
{
    private readonly JwtOptions _o = options.Value;

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_o.RefreshTokenDays);

    public string CreateAccessToken(ApplicationUser user, IEnumerable<string> roles, int? employeeId, out DateTime expiresAt)
    {
        var now = time.GetUtcNow().UtcDateTime;
        expiresAt = now.AddMinutes(_o.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (employeeId is int e) claims.Add(new Claim(AppClaims.EmployeeId, e.ToString()));
        if (user.MustChangePassword) claims.Add(new Claim(AppClaims.MustChangePassword, "1"));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey));
        var token = new JwtSecurityToken(_o.Issuer, _o.Audience, claims, now, expiresAt, new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));

    public string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// On-site verification for check-in/out: geofence (GPS within radius of HQ), company network (IP allow-list)
/// and rotating HMAC-signed QR tokens displayed on a kiosk screen.
/// </summary>
public class AttendanceVerifier(TimeProvider time) : IAttendanceVerifier
{
    public VerificationResult Verify(CompanySettings s, VerificationInput input)
    {
        var enabled = s.EnabledMethods;
        if (enabled == VerificationMethod.None) return new VerificationResult(true, VerificationMethod.None, null);

        var passed = VerificationMethod.None;
        var failures = new List<string>();

        if (s.GeofenceEnabled)
        {
            var (ok, reason) = CheckGeofence(s, input);
            if (ok) passed |= VerificationMethod.Geofence; else failures.Add(reason!);
        }
        if (s.NetworkCheckEnabled)
        {
            var (ok, reason) = CheckNetwork(s, input.IpAddress);
            if (ok) passed |= VerificationMethod.Network; else failures.Add(reason!);
        }
        if (s.QrCodeEnabled)
        {
            if (string.IsNullOrWhiteSpace(input.QrToken)) failures.Add("Scan the QR code displayed at the office entrance.");
            else if (ValidateQrToken(s, input.QrToken, time.GetUtcNow().UtcDateTime)) passed |= VerificationMethod.QrCode;
            else failures.Add("The QR code has expired or is invalid. Scan the current code again.");
        }

        var success = s.VerificationPolicy == VerificationPolicy.All ? passed == enabled : passed != VerificationMethod.None;
        return success
            ? new VerificationResult(true, passed, null)
            : new VerificationResult(false, passed, "Attendance can only be recorded from the company premises. " + string.Join(" ", failures));
    }

    private static (bool, string?) CheckGeofence(CompanySettings s, VerificationInput input)
    {
        if (s.OfficeLatitude is not double lat0 || s.OfficeLongitude is not double lon0) return (false, "Office location is not configured.");
        if (input.Latitude is not double lat || input.Longitude is not double lon)
            return (false, "Location access is required to verify you are at the office.");
        if (input.Accuracy is double acc && acc > s.MaxLocationAccuracyMeters)
            return (false, $"Your location is too imprecise ({acc:0} m). Enable precise location and try again.");
        var distance = HaversineMeters(lat0, lon0, lat, lon);
        return distance <= s.GeofenceRadiusMeters
            ? (true, null)
            : (false, $"You are about {distance:0} m from the office (allowed: {s.GeofenceRadiusMeters} m).");
    }

    private static (bool, string?) CheckNetwork(CompanySettings s, string? ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var address)) return (false, "Could not determine your network address.");
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var ranges = IpRangeParser.Parse(s.AllowedIpRanges);
        return ranges.Any(r => r.Contains(address)) ? (true, null) : (false, "You are not connected to the company network.");
    }

    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6_371_000;
        double ToRad(double d) => d * Math.PI / 180;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    // Token format: "{window}.{base64url(HMAC-SHA256(secret, window))[0..22]}"
    public string CreateQrToken(CompanySettings s, DateTime utcNow, out DateTime expiresAt)
    {
        var seconds = Math.Max(10, s.QrRotationSeconds);
        var window = new DateTimeOffset(utcNow, TimeSpan.Zero).ToUnixTimeSeconds() / seconds;
        expiresAt = DateTimeOffset.FromUnixTimeSeconds((window + 1) * seconds).UtcDateTime;
        return $"{window}.{Sign(s.QrSecret, window)}";
    }

    public bool ValidateQrToken(CompanySettings s, string token, DateTime utcNow)
    {
        var parts = token.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var window)) return false;
        var seconds = Math.Max(10, s.QrRotationSeconds);
        var current = new DateTimeOffset(utcNow, TimeSpan.Zero).ToUnixTimeSeconds() / seconds;
        // Accept the current and the previous window to tolerate scan / network latency.
        if (window != current && window != current - 1) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(s.QrSecret, window));
        var actual = Encoding.ASCII.GetBytes(parts[1]);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Sign(string secret, long window)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.ASCII.GetBytes($"attendance-qr:{window}"));
        return Base64UrlEncoder.Encode(hash)[..22];
    }

    public byte[] RenderQrPng(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(12);
    }
}
