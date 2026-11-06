using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Organization;

public record TeamMemberDto(int EmployeeId, string EmployeeCode, string FullName, string? JobTitle, DateOnly JoinedOn);

public record TeamDto(int Id, string Name, string? NameAr, string? Description, bool IsActive, int DepartmentId,
    string DepartmentName, int? LeaderEmployeeId, string? LeaderName, int MemberCount);

public record TeamDetailsDto(TeamDto Team, IReadOnlyList<TeamMemberDto> Members);

public record SaveTeamRequest(string Name, string? NameAr, string? Description, int DepartmentId, int? LeaderEmployeeId,
    int[]? MemberEmployeeIds, bool IsActive = true);

public class SaveTeamRequestValidator : AbstractValidator<SaveTeamRequest>
{
    public SaveTeamRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.NameAr).MaximumLength(150);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.DepartmentId).GreaterThan(0);
    }
}

public interface ITeamService
{
    Task<IReadOnlyList<TeamDto>> GetAllAsync(int? departmentId, CancellationToken ct = default);
    Task<IReadOnlyList<LookupDto>> LookupAsync(int? departmentId, CancellationToken ct = default);
    Task<TeamDetailsDto> GetAsync(int id, CancellationToken ct = default);
    Task<TeamDetailsDto> CreateAsync(SaveTeamRequest request, CancellationToken ct = default);
    Task<TeamDetailsDto> UpdateAsync(int id, SaveTeamRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task AddMemberAsync(int teamId, int employeeId, CancellationToken ct = default);
    Task RemoveMemberAsync(int teamId, int employeeId, CancellationToken ct = default);
}

public class TeamService(IApplicationDbContext db, IAuditService audit, ICompanyClock clock, IAccessScope scope) : ITeamService
{
    private static IQueryable<TeamDto> Project(IQueryable<Team> q) => q.Select(t => new TeamDto(
        t.Id, t.Name, t.NameAr, t.Description, t.IsActive, t.DepartmentId, t.Department.Name, t.LeaderEmployeeId,
        t.Leader != null ? t.Leader.FirstName + " " + t.Leader.LastName : null, t.Members.Count));

    public async Task<IReadOnlyList<TeamDto>> GetAllAsync(int? departmentId, CancellationToken ct = default)
    {
        var q = db.Teams.AsQueryable();
        if (departmentId is int d) q = q.Where(t => t.DepartmentId == d);
        return await Project(q.OrderBy(t => t.Department.Name).ThenBy(t => t.Name)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LookupDto>> LookupAsync(int? departmentId, CancellationToken ct = default)
    {
        var allowed = await scope.AccessibleTeamIdsAsync(ct);
        var q = db.Teams.Where(t => t.IsActive);
        if (departmentId is int d) q = q.Where(t => t.DepartmentId == d);
        if (allowed is not null) q = q.Where(t => allowed.Contains(t.Id));
        return await q.OrderBy(t => t.Name).Select(t => new LookupDto(t.Id, t.Name, null)).ToListAsync(ct);
    }

    public async Task<TeamDetailsDto> GetAsync(int id, CancellationToken ct = default)
    {
        var team = await Project(db.Teams.Where(t => t.Id == id)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Team", id);
        var members = await db.TeamMembers.Where(m => m.TeamId == id && !m.Employee.IsDeleted)
            .OrderBy(m => m.Employee.FirstName)
            .Select(m => new TeamMemberDto(m.EmployeeId, m.Employee.EmployeeCode, m.Employee.FirstName + " " + m.Employee.LastName,
                m.Employee.JobTitle, m.JoinedOn))
            .ToListAsync(ct);
        return new TeamDetailsDto(team, members);
    }

    public async Task<TeamDetailsDto> CreateAsync(SaveTeamRequest r, CancellationToken ct = default)
    {
        await ValidateAsync(null, r, ct);
        var t = new Team();
        Apply(t, r);
        db.Teams.Add(t);
        await db.SaveChangesAsync(ct);
        await SyncMembersAsync(t.Id, r, ct);
        audit.Log("Create", nameof(Team), t.Id, newValues: r);
        await db.SaveChangesAsync(ct);
        return await GetAsync(t.Id, ct);
    }

    public async Task<TeamDetailsDto> UpdateAsync(int id, SaveTeamRequest r, CancellationToken ct = default)
    {
        var t = await db.Teams.FindAsync([id], ct) ?? throw new NotFoundException("Team", id);
        await ValidateAsync(id, r, ct);
        var old = new { t.Name, t.DepartmentId, t.LeaderEmployeeId, t.IsActive };
        Apply(t, r);
        if (r.MemberEmployeeIds is not null) await SyncMembersAsync(id, r, ct);
        audit.Log("Update", nameof(Team), id, old, r);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var t = await db.Teams.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Team", id);
        db.TeamMembers.RemoveRange(t.Members);
        db.Teams.Remove(t);
        audit.Log("Delete", nameof(Team), id, new { t.Name, t.DepartmentId });
        await db.SaveChangesAsync(ct);
    }

    public async Task AddMemberAsync(int teamId, int employeeId, CancellationToken ct = default)
    {
        if (!await db.Teams.AnyAsync(t => t.Id == teamId, ct)) throw new NotFoundException("Team", teamId);
        if (!await db.Employees.AnyAsync(e => e.Id == employeeId && !e.IsDeleted, ct)) throw new NotFoundException("Employee", employeeId);
        if (await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.EmployeeId == employeeId, ct)) return;
        db.TeamMembers.Add(new TeamMember { TeamId = teamId, EmployeeId = employeeId, JoinedOn = await clock.TodayAsync(ct) });
        audit.Log("AddMember", nameof(Team), teamId, newValues: new { employeeId });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveMemberAsync(int teamId, int employeeId, CancellationToken ct = default)
    {
        var m = await db.TeamMembers.FirstOrDefaultAsync(x => x.TeamId == teamId && x.EmployeeId == employeeId, ct)
                ?? throw new NotFoundException("TeamMember", $"{teamId}/{employeeId}");
        db.TeamMembers.Remove(m);
        audit.Log("RemoveMember", nameof(Team), teamId, new { employeeId });
        await db.SaveChangesAsync(ct);
    }

    private async Task SyncMembersAsync(int teamId, SaveTeamRequest r, CancellationToken ct)
    {
        if (r.MemberEmployeeIds is null) return;
        var desired = r.MemberEmployeeIds.Distinct().ToHashSet();
        var valid = await db.Employees.Where(e => desired.Contains(e.Id) && !e.IsDeleted).Select(e => e.Id).ToListAsync(ct);
        if (valid.Count != desired.Count) throw new BadRequestException("One or more team members do not exist.");
        var current = await db.TeamMembers.Where(m => m.TeamId == teamId).ToListAsync(ct);
        db.TeamMembers.RemoveRange(current.Where(m => !desired.Contains(m.EmployeeId)));
        var today = await clock.TodayAsync(ct);
        foreach (var empId in desired.Except(current.Select(m => m.EmployeeId)))
            db.TeamMembers.Add(new TeamMember { TeamId = teamId, EmployeeId = empId, JoinedOn = today });
    }

    private async Task ValidateAsync(int? id, SaveTeamRequest r, CancellationToken ct)
    {
        if (!await db.Departments.AnyAsync(d => d.Id == r.DepartmentId, ct)) throw new NotFoundException("Department", r.DepartmentId);
        if (await db.Teams.AnyAsync(t => t.DepartmentId == r.DepartmentId && t.Name == r.Name && t.Id != id, ct))
            throw new ConflictException("A team with this name already exists in the department.");
        if (r.LeaderEmployeeId is int l && !await db.Employees.AnyAsync(e => e.Id == l && !e.IsDeleted, ct))
            throw new NotFoundException("Employee", l);
    }

    private static void Apply(Team t, SaveTeamRequest r)
    {
        t.Name = r.Name.Trim();
        t.NameAr = r.NameAr?.Trim();
        t.Description = r.Description;
        t.DepartmentId = r.DepartmentId;
        t.LeaderEmployeeId = r.LeaderEmployeeId;
        t.IsActive = r.IsActive;
    }
}
