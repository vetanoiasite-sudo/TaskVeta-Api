using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Common;

/// <summary>
/// EF Core context abstraction. DbSet&lt;T&gt; acts as the repository and SaveChangesAsync as the unit of work.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<ApplicationUser> Users { get; }
    DbSet<ApplicationRole> Roles { get; }
    DbSet<Microsoft.AspNetCore.Identity.IdentityUserRole<int>> UserRoles { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Employee> Employees { get; }
    DbSet<Department> Departments { get; }
    DbSet<Team> Teams { get; }
    DbSet<TeamMember> TeamMembers { get; }
    DbSet<AttendanceRecord> AttendanceRecords { get; }
    DbSet<WorkSchedule> WorkSchedules { get; }
    DbSet<Holiday> Holidays { get; }
    DbSet<LeaveRequest> LeaveRequests { get; }
    DbSet<Project> Projects { get; }
    DbSet<TaskItem> Tasks { get; }
    DbSet<TaskAssignment> TaskAssignments { get; }
    DbSet<TaskComment> TaskComments { get; }
    DbSet<TaskView> TaskViews { get; }
    DbSet<TaskReview> TaskReviews { get; }
    DbSet<TaskAttachment> TaskAttachments { get; }
    DbSet<TaskHistory> TaskHistory { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<CompanySettings> CompanySettings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default);
}

public interface ICurrentUser
{
    int? UserId { get; }
    string? UserName { get; }
    string? IpAddress { get; }
    bool IsAuthenticated { get; }
    IReadOnlyCollection<string> Roles { get; }
    bool IsInRole(string role);
    bool HasPermission(string permission);
}

/// <summary>Clock aware of the company's configured time zone.</summary>
public interface ICompanyClock
{
    DateTime UtcNow { get; }
    Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken ct = default);
    Task<DateTimeOffset> NowAsync(CancellationToken ct = default);
    Task<DateOnly> TodayAsync(CancellationToken ct = default);
    DateTimeOffset ToCompanyTime(DateTime utc, TimeZoneInfo tz);
    DateTimeOffset ToCompanyTime(DateOnly date, TimeOnly time, TimeZoneInfo tz);
    void InvalidateCache();
}

public interface IAuditService
{
    /// <summary>Adds an audit entry to the context; it is persisted with the next SaveChanges.</summary>
    void Log(string action, string entityName, object? entityId, object? oldValues = null, object? newValues = null, string? reason = null);
    /// <summary>Writes an audit entry immediately (used for security events outside a business transaction).</summary>
    Task LogNowAsync(string action, string entityName, object? entityId, object? newValues = null, string? reason = null, CancellationToken ct = default);
}

public interface INotificationService
{
    /// <summary>Queues notifications in the context. Call <see cref="DispatchAsync"/> after SaveChanges to push them in real time.</summary>
    void Add(int userId, NotificationType type, string title, string message, string? titleAr = null, string? messageAr = null,
        string? link = null, string? entityType = null, int? entityId = null);
    void AddForEmployees(IEnumerable<int> employeeIds, NotificationType type, string title, string message, string? titleAr = null,
        string? messageAr = null, string? link = null, string? entityType = null, int? entityId = null);
    Task SaveAndDispatchAsync(CancellationToken ct = default);
}

public interface IRealtimeNotifier
{
    Task PushAsync(int userId, object payload, CancellationToken ct = default);
}

public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default);
    Stream OpenRead(string storedFileName);
    void Delete(string storedFileName);
}

public interface ITokenService
{
    string CreateAccessToken(ApplicationUser user, IEnumerable<string> roles, int? employeeId, out DateTime expiresAt);
    string GenerateRefreshToken();
    string HashToken(string token);
    TimeSpan RefreshTokenLifetime { get; }
}

public record ExportColumn<T>(string Header, Func<T, object?> Value);

public interface IExportService
{
    byte[] ToExcel<T>(string sheetTitle, IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns);
    byte[] ToPdf<T>(string title, string? subtitle, IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns);
}

public record ExportFile(byte[] Content, string ContentType, string FileName);

public record VerificationInput(string? IpAddress, double? Latitude, double? Longitude, double? Accuracy, string? QrToken);

public record VerificationResult(bool Success, VerificationMethod Passed, string? FailureReason);

public interface IAttendanceVerifier
{
    VerificationResult Verify(CompanySettings settings, VerificationInput input);
    string CreateQrToken(CompanySettings settings, DateTime utcNow, out DateTime expiresAt);
    bool ValidateQrToken(CompanySettings settings, string token, DateTime utcNow);
    byte[] RenderQrPng(string content);
}
