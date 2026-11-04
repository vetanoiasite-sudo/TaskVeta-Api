using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Common;

public enum ScopeLevel { Self, Managed, All }

public interface IAccessScope
{
    Task<int?> GetCurrentEmployeeIdAsync(CancellationToken ct = default);
    ScopeLevel Level { get; }
    /// <summary>Employees (not deleted) that the current user may see.</summary>
    Task<IQueryable<Employee>> ScopedEmployeesAsync(CancellationToken ct = default);
    Task<bool> CanAccessEmployeeAsync(int employeeId, CancellationToken ct = default);
    Task EnsureCanAccessEmployeeAsync(int employeeId, CancellationToken ct = default);
    /// <summary>Department ids the current user may filter by (null = all).</summary>
    Task<int[]?> AccessibleDepartmentIdsAsync(CancellationToken ct = default);
}

/// <summary>
/// Server-side data scoping. SuperAdmin/Admin see everything, Managers see employees in the departments they
/// manage and the teams they lead (plus themselves), Employees see only themselves.
/// </summary>
public class AccessScope(IApplicationDbContext db, ICurrentUser user) : IAccessScope
{
    private int? _employeeId;
    private bool _employeeLoaded;

    public ScopeLevel Level =>
        user.HasPermission(Permissions.AttendanceViewAll) ? ScopeLevel.All :
        user.HasPermission(Permissions.AttendanceViewTeam) ? ScopeLevel.Managed :
        ScopeLevel.Self;

    public async Task<int?> GetCurrentEmployeeIdAsync(CancellationToken ct = default)
    {
        if (_employeeLoaded) return _employeeId;
        if (user.UserId is int uid)
            _employeeId = await db.Employees.Where(e => e.UserId == uid && !e.IsDeleted)
                .Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        _employeeLoaded = true;
        return _employeeId;
    }

    public async Task<IQueryable<Employee>> ScopedEmployeesAsync(CancellationToken ct = default)
    {
        var query = db.Employees.Where(e => !e.IsDeleted);
        switch (Level)
        {
            case ScopeLevel.All:
                return query;
            case ScopeLevel.Managed:
            {
                var me = await GetCurrentEmployeeIdAsync(ct) ?? -1;
                return query.Where(e => e.Id == me
                    || e.Department.ManagerEmployeeId == me
                    || e.TeamMemberships.Any(tm => tm.Team.LeaderEmployeeId == me));
            }
            default:
            {
                var me = await GetCurrentEmployeeIdAsync(ct) ?? -1;
                return query.Where(e => e.Id == me);
            }
        }
    }

    public async Task<bool> CanAccessEmployeeAsync(int employeeId, CancellationToken ct = default)
    {
        if (Level == ScopeLevel.All) return true;
        var scoped = await ScopedEmployeesAsync(ct);
        return await scoped.AnyAsync(e => e.Id == employeeId, ct);
    }

    public async Task EnsureCanAccessEmployeeAsync(int employeeId, CancellationToken ct = default)
    {
        if (!await CanAccessEmployeeAsync(employeeId, ct))
            throw new ForbiddenException("You cannot access records of this employee.");
    }

    public async Task<int[]?> AccessibleDepartmentIdsAsync(CancellationToken ct = default)
    {
        if (Level == ScopeLevel.All) return null;
        var scoped = await ScopedEmployeesAsync(ct);
        return await scoped.Select(e => e.DepartmentId).Distinct().ToArrayAsync(ct);
    }
}
