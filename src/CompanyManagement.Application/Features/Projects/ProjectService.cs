using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Projects;

/// <summary>Task counts cover all tasks of the project.</summary>
public record ProjectDto(int Id, string Name, string? NameAr, string Code, string? Description, ProjectStatus Status,
    DateOnly? StartDate, DateOnly? EndDate, int TaskCount, int CompletedTaskCount, int OverdueTaskCount, int ProgressPercent,
    DateTime CreatedAt, DateTime UpdatedAt);

public record ProjectLookupDto(int Id, string Name, string? NameAr, string Code, ProjectStatus Status);

public record SaveProjectRequest(string Name, string? NameAr, string Code, string? Description, ProjectStatus Status,
    DateOnly? StartDate, DateOnly? EndDate);

/// <summary>The project with all of its tasks, ordered for a Gantt-style timeline. <c>OpenableTaskIds</c>: tasks the user may open.</summary>
public record ProjectTimelineDto(ProjectDto Project, IReadOnlyList<TaskDto> Tasks, int[] OpenableTaskIds);

public class SaveProjectRequestValidator : AbstractValidator<SaveProjectRequest>
{
    public SaveProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.NameAr).MaximumLength(150);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20).Matches("^[A-Za-z0-9_-]+$");
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.EndDate).GreaterThanOrEqualTo(x => x.StartDate).When(x => x.StartDate.HasValue && x.EndDate.HasValue)
            .WithMessage("End date must be on or after the start date.");
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
}

public class ProjectService(IApplicationDbContext db, IAuditService audit, ITaskService tasks, ICurrentUser currentUser) : IProjectService
{
    public async Task<IReadOnlyList<ProjectDto>> GetAllAsync(CancellationToken ct = default)
    {
        var allowed = await tasks.AccessibleProjectIdsAsync(ct);
        var query = db.Projects.AsNoTracking();
        if (allowed is not null) query = query.Where(p => allowed.Contains(p.Id));
        var projects = await query.OrderBy(p => p.Status == ProjectStatus.Completed || p.Status == ProjectStatus.Cancelled)
            .ThenBy(p => p.Name).ToListAsync(ct);
        var stats = await tasks.GetProjectStatsAsync(null, ct);
        return projects.Select(p => ToDto(p, stats.GetValueOrDefault(p.Id))).ToList();
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
        return ToDto(p, stats.GetValueOrDefault(id));
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
        await EnsureUniqueCodeAsync(null, r.Code, ct);
        var p = new Project();
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
        await EnsureUniqueCodeAsync(id, r.Code, ct);
        var old = new { p.Name, p.NameAr, p.Code, p.Description, Status = p.Status.ToString(), p.StartDate, p.EndDate };
        Apply(p, r);
        audit.Log("Update", nameof(Project), id, old, r);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Deletes the project; its tasks are kept and simply no longer belong to a project.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var p = await db.Projects.FindAsync([id], ct) ?? throw new NotFoundException("Project", id);
        var taskCount = await db.Tasks.CountAsync(t => t.ProjectId == id, ct);
        db.Projects.Remove(p); // FK is ON DELETE SET NULL: the tasks stay, without a project
        audit.Log("Delete", nameof(Project), id, new { p.Name, p.Code, UnlinkedTasks = taskCount });
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureUniqueCodeAsync(int? id, string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        if (await db.Projects.AnyAsync(p => p.Code == normalized && p.Id != id, ct))
            throw new ConflictException($"Project code '{normalized}' is already used.");
    }

    private static void Apply(Project p, SaveProjectRequest r)
    {
        p.Name = r.Name.Trim();
        p.NameAr = string.IsNullOrWhiteSpace(r.NameAr) ? null : r.NameAr.Trim();
        p.Code = r.Code.Trim().ToUpperInvariant();
        p.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        p.Status = r.Status;
        p.StartDate = r.StartDate;
        p.EndDate = r.EndDate;
    }

    private static ProjectDto ToDto(Project p, ProjectTaskStats? s) => new(
        p.Id, p.Name, p.NameAr, p.Code, p.Description, p.Status, p.StartDate, p.EndDate,
        s?.Total ?? 0, s?.Completed ?? 0, s?.Overdue ?? 0, s?.ProgressPercent ?? 0, p.CreatedAt, p.UpdatedAt);
}
