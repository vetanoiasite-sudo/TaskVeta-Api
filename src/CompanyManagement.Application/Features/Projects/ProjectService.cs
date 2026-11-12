using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Projects;

public record ProjectMemberDto(int Id, int EmployeeId, string Name, string EmployeeCode, int? UserId, ProjectRole Role);

/// <summary>Task counts cover all tasks of the project. <c>CanManage</c>: the current user may edit the project and its members.</summary>
public record ProjectDto(int Id, string Name, string? NameAr, string Code, string? Description, ProjectStatus Status,
    DateOnly? StartDate, DateOnly? EndDate, int TaskCount, int CompletedTaskCount, int OverdueTaskCount, int ProgressPercent,
    DateTime CreatedAt, DateTime UpdatedAt, string? ClientName, string? RepositoryUrl, string? DesignUrl, string? DocsUrl,
    int? CreatedByUserId, IReadOnlyList<ProjectMemberDto> Members, bool CanManage, bool CanLead, ProjectRole[] MyRoles);

public record ProjectLookupDto(int Id, string Name, string? NameAr, string Code, ProjectStatus Status);

public record SaveProjectRequest(string Name, string? NameAr, string Code, string? Description, ProjectStatus Status,
    DateOnly? StartDate, DateOnly? EndDate, string? ClientName = null, string? RepositoryUrl = null, string? DesignUrl = null,
    string? DocsUrl = null);

public record AddProjectMemberRequest(int EmployeeId, ProjectRole Role);

public record ProjectMemberCandidateDto(int EmployeeId, string Name, string EmployeeCode, string? DepartmentName);

/// <summary>The project with all of its tasks, ordered for a Gantt-style timeline. <c>OpenableTaskIds</c>: tasks the user may open.</summary>
public record ProjectTimelineDto(ProjectDto Project, IReadOnlyList<TaskDto> Tasks, int[] OpenableTaskIds);

public class SaveProjectRequestValidator : AbstractValidator<SaveProjectRequest>
{
    public SaveProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.NameAr).MaximumLength(150);
        // Codes prefix task keys (GA-123). Kept compatible with codes created before keys existed.
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20).Matches("^[A-Za-z0-9_-]+$");
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.EndDate).GreaterThanOrEqualTo(x => x.StartDate).When(x => x.StartDate.HasValue && x.EndDate.HasValue)
            .WithMessage("End date must be on or after the start date.");
        RuleFor(x => x.ClientName).MaximumLength(150);
        RuleForEach(x => new[] { x.RepositoryUrl, x.DesignUrl, x.DocsUrl })
            .Must(u => string.IsNullOrWhiteSpace(u) || (u.Length <= 500 && IsHttpUrl(u.Trim())))
            .OverridePropertyName("Links").WithMessage("Links must be valid http(s) URLs.");
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}

public class AddProjectMemberRequestValidator : AbstractValidator<AddProjectMemberRequest>
{
    public AddProjectMemberRequestValidator()
    {
        RuleFor(x => x.EmployeeId).GreaterThan(0);
        RuleFor(x => x.Role).IsInEnum();
    }
}

public interface IProjectService
{
    Task<IReadOnlyList<ProjectDto>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProjectLookupDto>> LookupAsync(CancellationToken ct = default);
    Task<ProjectDto> GetAsync(int id, CancellationToken ct = default);
    Task<ProjectTimelineDto> GetTimelineAsync(int id, CancellationToken ct = default);
    Task<ProjectDto> CreateAsync(SaveProjectRequest request, CancellationToken ct = default);
    Task<ProjectDto> UpdateAsync(int id, SaveProjectRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<ProjectDto> AddMemberAsync(int id, AddProjectMemberRequest request, CancellationToken ct = default);
    Task<ProjectDto> RemoveMemberAsync(int id, int memberId, CancellationToken ct = default);
    /// <summary>Active employees that can be added as members (only for people who manage the project).</summary>
    Task<IReadOnlyList<ProjectMemberCandidateDto>> MemberCandidatesAsync(int id, CancellationToken ct = default);
}

public class ProjectService(
    IApplicationDbContext db,
    IAuditService audit,
    ITaskService tasks,
    IProjectAccess access,
    INotificationService notifications,
    ICurrentUser currentUser) : IProjectService
{
    public async Task<IReadOnlyList<ProjectDto>> GetAllAsync(CancellationToken ct = default)
    {
        var allowed = await tasks.AccessibleProjectIdsAsync(ct);
        var query = db.Projects.AsNoTracking();
        if (allowed is not null) query = query.Where(p => allowed.Contains(p.Id));
        var projects = await query.OrderBy(p => p.Status == ProjectStatus.Completed || p.Status == ProjectStatus.Cancelled)
            .ThenBy(p => p.Name).ToListAsync(ct);
        var stats = await tasks.GetProjectStatsAsync(null, ct);
        var members = await MembersAsync(projects.Select(p => p.Id).ToArray(), ct);
        var result = new List<ProjectDto>(projects.Count);
        foreach (var p in projects)
            result.Add(await ToDtoAsync(p, stats.GetValueOrDefault(p.Id), members.GetValueOrDefault(p.Id) ?? [], ct));
        return result;
    }

    /// <summary>Task assigners get every project (to pick one in the task form); others only the projects they take part in.</summary>
    public async Task<IReadOnlyList<ProjectLookupDto>> LookupAsync(CancellationToken ct = default)
    {
        var query = db.Projects.AsNoTracking();
        if (!currentUser.HasPermission(Permissions.TasksAssign) && await tasks.AccessibleProjectIdsAsync(ct) is { } allowed)
            query = query.Where(p => allowed.Contains(p.Id));
        return await query.OrderBy(p => p.Name).Select(p => new ProjectLookupDto(p.Id, p.Name, p.NameAr, p.Code, p.Status)).ToListAsync(ct);
    }

    public async Task<ProjectDto> GetAsync(int id, CancellationToken ct = default)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Project", id);
        if (await tasks.AccessibleProjectIdsAsync(ct) is { } allowed && !allowed.Contains(id)) throw new NotFoundException("Project", id);
        var stats = await tasks.GetProjectStatsAsync(id, ct);
        var members = await MembersAsync([id], ct);
        return await ToDtoAsync(p, stats.GetValueOrDefault(id), members.GetValueOrDefault(id) ?? [], ct);
    }

    public async Task<ProjectTimelineDto> GetTimelineAsync(int id, CancellationToken ct = default)
    {
        var project = await GetAsync(id, ct);
        var (list, openable) = await tasks.GetProjectTasksAsync(id, ct);
        var ordered = list.OrderBy(t => t.StartDate ?? t.CreatedAt).ThenBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.Id).ToList();
        return new ProjectTimelineDto(project, ordered, openable);
    }

    public async Task<ProjectDto> CreateAsync(SaveProjectRequest r, CancellationToken ct = default)
    {
        if (!currentUser.HasPermission(Permissions.ProjectsCreate) && !currentUser.HasPermission(Permissions.ProjectsManage))
            throw new ForbiddenException("You cannot create projects.");
        await EnsureUniqueCodeAsync(null, r.Code, ct);
        var p = new Project { CreatedByUserId = currentUser.UserId };
        Apply(p, r);
        db.Projects.Add(p);
        await db.SaveChangesAsync(ct);
        audit.Log("Create", nameof(Project), p.Id, newValues: r);
        await db.SaveChangesAsync(ct);
        return await GetAsync(p.Id, ct);
    }

    public async Task<ProjectDto> UpdateAsync(int id, SaveProjectRequest r, CancellationToken ct = default)
    {
        var p = await db.Projects.FindAsync([id], ct) ?? throw new NotFoundException("Project", id);
        await EnsureCanManageAsync(id, ct);
        await EnsureUniqueCodeAsync(id, r.Code, ct);
        if (!string.Equals(p.Code, r.Code.Trim(), StringComparison.OrdinalIgnoreCase) && await db.Tasks.AnyAsync(t => t.ProjectId == id, ct))
            throw new BadRequestException("The project code cannot change once it has tasks: it is part of every task key.");
        var old = new { p.Name, p.NameAr, p.Code, p.Description, Status = p.Status.ToString(), p.StartDate, p.EndDate, p.ClientName, p.RepositoryUrl, p.DesignUrl, p.DocsUrl };
        Apply(p, r);
        audit.Log("Update", nameof(Project), id, old, r);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Deletes the project; its tasks are kept and simply no longer belong to a project. Admins and the creator only.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var p = await db.Projects.FindAsync([id], ct) ?? throw new NotFoundException("Project", id);
        if (!currentUser.HasPermission(Permissions.ProjectsManage) && p.CreatedByUserId != currentUser.UserId)
            throw new ForbiddenException("Only admins and the project's creator can delete it.");
        var taskCount = await db.Tasks.CountAsync(t => t.ProjectId == id, ct);
        // Tasks lose their project (FK ON DELETE SET NULL) and with it their key number.
        foreach (var t in await db.Tasks.Where(t => t.ProjectId == id).ToListAsync(ct)) t.Number = null;
        db.Projects.Remove(p);
        audit.Log("Delete", nameof(Project), id, new { p.Name, p.Code, UnlinkedTasks = taskCount });
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProjectDto> AddMemberAsync(int id, AddProjectMemberRequest r, CancellationToken ct = default)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Project", id);
        await EnsureCanManageAsync(id, ct);
        var employee = await db.Employees.AsNoTracking().Where(e => e.Id == r.EmployeeId && !e.IsDeleted && e.IsActive)
                           .Select(e => new { e.Id, e.UserId, Name = e.FirstName + " " + e.LastName }).FirstOrDefaultAsync(ct)
                       ?? throw new BadRequestException("The employee does not exist or is inactive.");
        if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == id && m.EmployeeId == r.EmployeeId && m.Role == r.Role, ct))
            return await GetAsync(id, ct);

        db.ProjectMembers.Add(new ProjectMember { ProjectId = id, EmployeeId = r.EmployeeId, Role = r.Role });
        audit.Log("AddMember", nameof(Project), id, newValues: new { r.EmployeeId, Role = r.Role.ToString() });
        if (employee.UserId is int uid && uid != currentUser.UserId)
            notifications.Add(uid, NotificationType.ProjectMembership, "Added to a project",
                $"You were added to {project.Name} ({project.Code}) as {r.Role}.", "تمت إضافتك إلى مشروع",
                $"تمت إضافتك إلى مشروع {project.NameAr ?? project.Name} ({project.Code}).", $"/projects/{id}", "Project", id);
        await notifications.SaveAndDispatchAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<ProjectDto> RemoveMemberAsync(int id, int memberId, CancellationToken ct = default)
    {
        await EnsureCanManageAsync(id, ct);
        var member = await db.ProjectMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.ProjectId == id, ct)
                     ?? throw new NotFoundException("Project member", memberId);
        // Keep at least one way to manage the project: don't remove the last BR Owner of a project without a creator.
        if (member.Role == ProjectRole.BrOwner
            && !await db.Projects.AnyAsync(p => p.Id == id && p.CreatedByUserId != null, ct)
            && await db.ProjectMembers.CountAsync(m => m.ProjectId == id && m.Role == ProjectRole.BrOwner, ct) == 1
            && !currentUser.HasPermission(Permissions.ProjectsManage))
            throw new BadRequestException("Add another BR Owner first: the project needs someone to manage it.");
        db.ProjectMembers.Remove(member);
        audit.Log("RemoveMember", nameof(Project), id, new { member.EmployeeId, Role = member.Role.ToString() });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<ProjectMemberCandidateDto>> MemberCandidatesAsync(int id, CancellationToken ct = default)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == id, ct)) throw new NotFoundException("Project", id);
        await EnsureCanManageAsync(id, ct);
        return await db.Employees.AsNoTracking().Where(e => !e.IsDeleted && e.IsActive).OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new ProjectMemberCandidateDto(e.Id, e.FirstName + " " + e.LastName, e.EmployeeCode, e.Department.Name)).ToListAsync(ct);
    }

    private async Task EnsureCanManageAsync(int id, CancellationToken ct)
    {
        if (!await access.CanManageAsync(id, ct)) throw new ForbiddenException("You cannot manage this project.");
    }

    private async Task<Dictionary<int, List<ProjectMemberDto>>> MembersAsync(int[] projectIds, CancellationToken ct)
    {
        var rows = await db.ProjectMembers.AsNoTracking().Where(m => projectIds.Contains(m.ProjectId))
            .OrderBy(m => m.Role).ThenBy(m => m.Employee.FirstName)
            .Select(m => new { m.ProjectId, Dto = new ProjectMemberDto(m.Id, m.EmployeeId, m.Employee.FirstName + " " + m.Employee.LastName,
                m.Employee.EmployeeCode, m.Employee.UserId, m.Role) })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.ProjectId).ToDictionary(g => g.Key, g => g.Select(r => r.Dto).ToList());
    }

    private async Task EnsureUniqueCodeAsync(int? id, string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        if (await db.Projects.AnyAsync(p => p.Code == normalized && p.Id != id, ct))
            throw new ConflictException($"Project code '{normalized}' is already used.");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void Apply(Project p, SaveProjectRequest r)
    {
        p.Name = r.Name.Trim();
        p.NameAr = Clean(r.NameAr);
        p.Code = r.Code.Trim().ToUpperInvariant();
        p.Description = Clean(r.Description);
        p.Status = r.Status;
        p.StartDate = r.StartDate;
        p.EndDate = r.EndDate;
        p.ClientName = Clean(r.ClientName);
        p.RepositoryUrl = Clean(r.RepositoryUrl);
        p.DesignUrl = Clean(r.DesignUrl);
        p.DocsUrl = Clean(r.DocsUrl);
    }

    private async Task<ProjectDto> ToDtoAsync(Project p, ProjectTaskStats? s, List<ProjectMemberDto> members, CancellationToken ct)
    {
        var myRoles = (await access.MyRolesAsync(ct)).GetValueOrDefault(p.Id) ?? [];
        var canManage = currentUser.HasPermission(Permissions.ProjectsManage) || p.CreatedByUserId == currentUser.UserId
                        || myRoles.Contains(ProjectRole.BrOwner);
        return new ProjectDto(p.Id, p.Name, p.NameAr, p.Code, p.Description, p.Status, p.StartDate, p.EndDate,
            s?.Total ?? 0, s?.Completed ?? 0, s?.Overdue ?? 0, s?.ProgressPercent ?? 0, p.CreatedAt, p.UpdatedAt,
            p.ClientName, p.RepositoryUrl, p.DesignUrl, p.DocsUrl, p.CreatedByUserId, members, canManage,
            canManage || myRoles.Any(ProjectMember.IsLeadRole), myRoles);
    }
}
