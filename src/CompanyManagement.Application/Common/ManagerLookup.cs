using CompanyManagement.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Common;

public static class ManagerLookup
{
    /// <summary>Employee ids of the department manager and team leaders responsible for an employee (excluding the employee).</summary>
    public static async Task<List<int>> GetManagerEmployeeIdsAsync(IApplicationDbContext db, int employeeId, CancellationToken ct)
    {
        var deptManager = await db.Employees.Where(e => e.Id == employeeId)
            .Select(e => e.Department.ManagerEmployeeId).FirstOrDefaultAsync(ct);
        var leaders = await db.TeamMembers.Where(m => m.EmployeeId == employeeId && m.Team.LeaderEmployeeId != null)
            .Select(m => m.Team.LeaderEmployeeId!.Value).ToListAsync(ct);
        if (deptManager is int d) leaders.Add(d);
        return leaders.Where(id => id != employeeId).Distinct().ToList();
    }

    /// <summary>User ids of everyone holding one of the given roles.</summary>
    public static async Task<List<int>> GetUserIdsInRolesAsync(IApplicationDbContext db, string[] roles, CancellationToken ct)
    {
        var roleIds = await db.Roles.Where(r => roles.Contains(r.Name!)).Select(r => r.Id).ToListAsync(ct);
        return await db.Users.Where(u => u.IsActive)
            .Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && roleIds.Contains(ur.RoleId)))
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    public static readonly string[] HrRoles = [Roles.SuperAdmin, Roles.Admin];
}
