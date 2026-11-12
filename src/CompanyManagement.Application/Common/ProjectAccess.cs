using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Common;

/// <summary>
/// The current user's place in projects. Project membership is independent of departments and teams:
/// <list type="bullet">
/// <item>Any member sees the project and all of its tasks.</item>
/// <item>Lead roles (BR Owner, UI/UX Owner, FE/BE/Mobile Lead, QA Owner) also create and review the project's tasks.</item>
/// <item>Admins with <c>projects.manage</c>, the project's creator and its BR Owners manage the project and its members.</item>
/// </list>
/// Results are cached for the request (the service is scoped).
/// </summary>
public interface IProjectAccess
{
    Task<IReadOnlyDictionary<int, ProjectRole[]>> MyRolesAsync(CancellationToken ct = default);
    Task<int[]> MemberProjectIdsAsync(CancellationToken ct = default);
    Task<int[]> LeadProjectIdsAsync(CancellationToken ct = default);
    Task<bool> IsLeadAsync(int projectId, CancellationToken ct = default);
    Task<bool> CanManageAsync(int projectId, CancellationToken ct = default);
}

public class ProjectAccess(IApplicationDbContext db, IAccessScope scope, ICurrentUser currentUser) : IProjectAccess
{
    private IReadOnlyDictionary<int, ProjectRole[]>? _roles;

    public async Task<IReadOnlyDictionary<int, ProjectRole[]>> MyRolesAsync(CancellationToken ct = default)
    {
        if (_roles is not null) return _roles;
        var me = await scope.GetCurrentEmployeeIdAsync(ct);
        if (me is not int id) return _roles = new Dictionary<int, ProjectRole[]>();
        var rows = await db.ProjectMembers.AsNoTracking().Where(m => m.EmployeeId == id)
            .Select(m => new { m.ProjectId, m.Role }).ToListAsync(ct);
        return _roles = rows.GroupBy(r => r.ProjectId).ToDictionary(g => g.Key, g => g.Select(r => r.Role).ToArray());
    }

    public async Task<int[]> MemberProjectIdsAsync(CancellationToken ct = default) => (await MyRolesAsync(ct)).Keys.ToArray();

    public async Task<int[]> LeadProjectIdsAsync(CancellationToken ct = default) =>
        (await MyRolesAsync(ct)).Where(kv => kv.Value.Any(ProjectMember.IsLeadRole)).Select(kv => kv.Key).ToArray();

    public async Task<bool> IsLeadAsync(int projectId, CancellationToken ct = default) =>
        (await MyRolesAsync(ct)).TryGetValue(projectId, out var roles) && roles.Any(ProjectMember.IsLeadRole);

    public async Task<bool> CanManageAsync(int projectId, CancellationToken ct = default)
    {
        if (currentUser.HasPermission(Permissions.ProjectsManage)) return true;
        if ((await MyRolesAsync(ct)).TryGetValue(projectId, out var roles) && roles.Contains(ProjectRole.BrOwner)) return true;
        var uid = currentUser.UserId ?? -1;
        return await db.Projects.AnyAsync(p => p.Id == projectId && p.CreatedByUserId == uid, ct);
    }
}
