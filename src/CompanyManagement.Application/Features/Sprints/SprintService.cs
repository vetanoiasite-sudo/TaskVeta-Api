using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Sprints;

/// <summary>Counts are over all tasks of the sprint.</summary>
public record SprintDto(int Id, string Name, string? Goal, DateOnly StartDate, DateOnly EndDate, SprintStatus Status, DateTime? CompletedAt,
    int TaskCount, int CompletedTaskCount, decimal EstimateDays, decimal CompletedEstimateDays, bool CanManage);

public record SaveSprintRequest(string Name, string? Goal, DateOnly StartDate, DateOnly EndDate);

/// <param name="MoveUnfinishedToSprintId">Where unfinished tasks go: another (planned or active) sprint, or null for the backlog.</param>
public record CompleteSprintRequest(int? MoveUnfinishedToSprintId);

public record PlanTasksRequest(int[] TaskIds);

public record SprintBreakdownRow(string Key, string Label, int Tasks, int Completed, decimal EstimateDays, decimal CompletedEstimateDays);

/// <param name="CapacitySplit">Share of estimated days: "new" (Feature), "improve" (Improvement + TechDebt), "bugs" (Bug), "other" (Design + Docs).</param>
/// <param name="Unestimated">Tasks without an estimate (they make the capacity numbers less reliable).</param>
public record SprintReportDto(SprintDto Sprint, int Total, int Completed, int InProgress, int InReview, int NotStarted, int Rejected,
    int Cancelled, int Blocked, int Overdue, int Unestimated, decimal EstimateDays, decimal CompletedEstimateDays, int CompletionPercent,
    IReadOnlyDictionary<string, int> CapacitySplit, IReadOnlyList<SprintBreakdownRow> ByType, IReadOnlyList<SprintBreakdownRow> ByAssignee,
    IReadOnlyList<SprintBreakdownRow> ByProject, IReadOnlyList<TaskDto> OpenTasks);

public class SaveSprintRequestValidator : AbstractValidator<SaveSprintRequest>
{
    public SaveSprintRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Goal).MaximumLength(1000);
        RuleFor(x => x.EndDate).GreaterThanOrEqualTo(x => x.StartDate).WithMessage("The sprint must end on or after its start date.");
        RuleFor(x => x).Must(x => x.EndDate.DayNumber - x.StartDate.DayNumber <= 60).WithMessage("A sprint cannot be longer than 60 days.");
    }
}

public class PlanTasksRequestValidator : AbstractValidator<PlanTasksRequest>
{
    public PlanTasksRequestValidator()
    {
        RuleFor(x => x.TaskIds).NotEmpty().Must(ids => ids.Length <= 200).WithMessage("Plan at most 200 tasks at once.");
    }
}

public interface ISprintService
{
    Task<IReadOnlyList<SprintDto>> GetAllAsync(bool includeCompleted, CancellationToken ct = default);
    Task<SprintDto> GetAsync(int id, CancellationToken ct = default);
    /// <summary>The active sprint, if any.</summary>
    Task<SprintDto?> GetActiveAsync(CancellationToken ct = default);
    Task<SprintDto> CreateAsync(SaveSprintRequest request, CancellationToken ct = default);
    Task<SprintDto> UpdateAsync(int id, SaveSprintRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<SprintDto> StartAsync(int id, CancellationToken ct = default);
    Task<SprintDto> CompleteAsync(int id, CompleteSprintRequest request, CancellationToken ct = default);
    /// <summary>Puts several tasks into the sprint at once (each must be visible to the user).</summary>
    Task<SprintDto> PlanTasksAsync(int id, PlanTasksRequest request, CancellationToken ct = default);
    Task<SprintReportDto> GetReportAsync(int id, CancellationToken ct = default);
}

public class SprintService(IApplicationDbContext db, ITaskService tasks, IAuditService audit, ICurrentUser currentUser, ICompanyClock clock)
    : ISprintService
{
    private bool CanManage => currentUser.HasPermission(Permissions.SprintsManage);

    private void EnsureCanManage()
    {
        if (!CanManage) throw new ForbiddenException("You cannot manage sprints.");
    }

    public async Task<IReadOnlyList<SprintDto>> GetAllAsync(bool includeCompleted, CancellationToken ct = default)
    {
        var query = db.Sprints.AsNoTracking();
        if (!includeCompleted) query = query.Where(s => s.Status != SprintStatus.Completed);
        return await Project(query.OrderByDescending(s => s.Status == SprintStatus.Active).ThenByDescending(s => s.StartDate)).ToListAsync(ct);
    }

    public async Task<SprintDto> GetAsync(int id, CancellationToken ct = default) =>
        await Project(db.Sprints.AsNoTracking().Where(s => s.Id == id)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Sprint", id);

    public async Task<SprintDto?> GetActiveAsync(CancellationToken ct = default) =>
        await Project(db.Sprints.AsNoTracking().Where(s => s.Status == SprintStatus.Active)).FirstOrDefaultAsync(ct);

    private IQueryable<SprintDto> Project(IQueryable<Sprint> q)
    {
        var canManage = CanManage;
        return q.Select(s => new SprintDto(s.Id, s.Name, s.Goal, s.StartDate, s.EndDate, s.Status, s.CompletedAt,
            s.Tasks.Count(), s.Tasks.Count(t => t.Status == TaskItemStatus.Completed),
            s.Tasks.Where(t => t.Status != TaskItemStatus.Cancelled).Sum(t => t.EstimateDays ?? 0),
            s.Tasks.Where(t => t.Status == TaskItemStatus.Completed).Sum(t => t.EstimateDays ?? 0), canManage));
    }

    public async Task<SprintDto> CreateAsync(SaveSprintRequest r, CancellationToken ct = default)
    {
        EnsureCanManage();
        await EnsureNoOverlapAsync(null, r, ct);
        var s = new Sprint { Name = r.Name.Trim(), Goal = Clean(r.Goal), StartDate = r.StartDate, EndDate = r.EndDate };
        db.Sprints.Add(s);
        await db.SaveChangesAsync(ct);
        audit.Log("Create", nameof(Sprint), s.Id, newValues: r);
        await db.SaveChangesAsync(ct);
        return await GetAsync(s.Id, ct);
    }

    public async Task<SprintDto> UpdateAsync(int id, SaveSprintRequest r, CancellationToken ct = default)
    {
        EnsureCanManage();
        var s = await db.Sprints.FindAsync([id], ct) ?? throw new NotFoundException("Sprint", id);
        if (s.Status == SprintStatus.Completed) throw new BadRequestException("Completed sprints cannot be edited.");
        await EnsureNoOverlapAsync(id, r, ct);
        var old = new { s.Name, s.Goal, s.StartDate, s.EndDate };
        s.Name = r.Name.Trim();
        s.Goal = Clean(r.Goal);
        s.StartDate = r.StartDate;
        s.EndDate = r.EndDate;
        audit.Log("Update", nameof(Sprint), id, old, r);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Only planned sprints can be deleted; their tasks go back to the backlog.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        EnsureCanManage();
        var s = await db.Sprints.FindAsync([id], ct) ?? throw new NotFoundException("Sprint", id);
        if (s.Status != SprintStatus.Planned) throw new BadRequestException("Only planned sprints can be deleted.");
        var taskCount = await db.Tasks.CountAsync(t => t.SprintId == id, ct);
        db.Sprints.Remove(s); // FK ON DELETE SET NULL: tasks return to the backlog
        audit.Log("Delete", nameof(Sprint), id, new { s.Name, ReturnedToBacklog = taskCount });
        await db.SaveChangesAsync(ct);
    }

    public async Task<SprintDto> StartAsync(int id, CancellationToken ct = default)
    {
        EnsureCanManage();
        var s = await db.Sprints.FindAsync([id], ct) ?? throw new NotFoundException("Sprint", id);
        if (s.Status != SprintStatus.Planned) throw new BadRequestException("Only planned sprints can be started.");
        if (await db.Sprints.AnyAsync(x => x.Status == SprintStatus.Active, ct))
            throw new ConflictException("Another sprint is active. Complete it before starting a new one.");
        s.Status = SprintStatus.Active;
        audit.Log("Start", nameof(Sprint), id);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("Another sprint is active. Complete it before starting a new one."); }
        return await GetAsync(id, ct);
    }

    public async Task<SprintDto> CompleteAsync(int id, CompleteSprintRequest r, CancellationToken ct = default)
    {
        EnsureCanManage();
        var s = await db.Sprints.FindAsync([id], ct) ?? throw new NotFoundException("Sprint", id);
        if (s.Status != SprintStatus.Active) throw new BadRequestException("Only the active sprint can be completed.");
        string? targetName = null;
        if (r.MoveUnfinishedToSprintId is int target)
        {
            if (target == id) throw new BadRequestException("Choose another sprint for the unfinished tasks.");
            targetName = await db.Sprints.Where(x => x.Id == target && x.Status != SprintStatus.Completed).Select(x => x.Name).FirstOrDefaultAsync(ct)
                         ?? throw new BadRequestException("Unfinished tasks can only move to a planned sprint.");
        }

        var unfinished = await db.Tasks.Where(t => t.SprintId == id && t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled)
            .ToListAsync(ct);
        foreach (var t in unfinished)
        {
            t.SprintId = r.MoveUnfinishedToSprintId;
            db.TaskHistory.Add(new TaskHistory
            {
                TaskId = t.Id, ChangedByUserId = currentUser.UserId, Action = "SprintChanged",
                Details = $"Carried over from {s.Name} " + (targetName is null ? "to the backlog" : $"to {targetName}")
            });
        }
        s.Status = SprintStatus.Completed;
        s.CompletedAt = clock.UtcNow;
        audit.Log("Complete", nameof(Sprint), id, newValues: new { CarriedOver = unfinished.Count, r.MoveUnfinishedToSprintId });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<SprintDto> PlanTasksAsync(int id, PlanTasksRequest r, CancellationToken ct = default)
    {
        EnsureCanManage();
        var s = await db.Sprints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Sprint", id);
        if (s.Status == SprintStatus.Completed) throw new BadRequestException("Tasks cannot be added to a completed sprint.");
        // Goes through the task service so visibility and history rules apply to every task.
        foreach (var taskId in r.TaskIds.Distinct())
            await tasks.SetSprintAsync(taskId, new SetTaskSprintRequest(id), ct);
        return await GetAsync(id, ct);
    }

    public async Task<SprintReportDto> GetReportAsync(int id, CancellationToken ct = default)
    {
        var sprint = await GetAsync(id, ct);
        var list = await tasks.GetSprintTasksAsync(id, ct);
        var live = list.Where(t => t.Status != TaskItemStatus.Cancelled).ToList();

        static decimal Days(IEnumerable<TaskDto> ts) => ts.Sum(t => t.EstimateDays ?? 0);
        static bool Done(TaskDto t) => t.Status == TaskItemStatus.Completed;

        var totalDays = Days(live);
        var doneDays = Days(live.Where(Done));
        int Pct(decimal part) => totalDays == 0 ? 0 : (int)Math.Round(part * 100 / totalDays);

        var split = new Dictionary<string, int>
        {
            ["new"] = Pct(Days(live.Where(t => t.Type == TaskType.Feature))),
            ["improve"] = Pct(Days(live.Where(t => t.Type is TaskType.Improvement or TaskType.TechDebt))),
            ["bugs"] = Pct(Days(live.Where(t => t.Type == TaskType.Bug))),
            ["other"] = Pct(Days(live.Where(t => t.Type is TaskType.Design or TaskType.Docs))),
        };

        SprintBreakdownRow Row(string key, string label, IEnumerable<TaskDto> ts)
        {
            var items = ts.ToList();
            return new SprintBreakdownRow(key, label, items.Count, items.Count(Done), Days(items), Days(items.Where(Done)));
        }

        var byType = live.GroupBy(t => t.Type).OrderBy(g => g.Key).Select(g => Row(g.Key.ToString(), g.Key.ToString(), g)).ToList();
        var byAssignee = live.SelectMany(t => t.Assignees.Count == 0
                ? [(Key: "unassigned", Label: "Unassigned", Task: t)]
                : t.Assignees.Select(a => (Key: a.EmployeeId.ToString(), Label: a.FullName, Task: t)))
            .GroupBy(x => (x.Key, x.Label)).OrderBy(g => g.Key.Label)
            .Select(g => Row(g.Key.Key, g.Key.Label, g.Select(x => x.Task))).ToList();
        var byProject = live.GroupBy(t => (Key: t.ProjectCode ?? "-", Label: t.ProjectName ?? "No project")).OrderBy(g => g.Key.Label)
            .Select(g => Row(g.Key.Key, g.Key.Label, g)).ToList();

        var open = list.Where(t => t.Status is not (TaskItemStatus.Completed or TaskItemStatus.Cancelled))
            .OrderByDescending(t => t.IsBlocked).ThenByDescending(t => t.Priority).ThenBy(t => t.DueDate ?? DateTime.MaxValue).ToList();

        return new SprintReportDto(sprint, list.Count, list.Count(Done),
            list.Count(t => t.Status == TaskItemStatus.InProgress), list.Count(t => t.Status == TaskItemStatus.PendingReview),
            list.Count(t => t.Status is TaskItemStatus.New or TaskItemStatus.Assigned), list.Count(t => t.Status == TaskItemStatus.Rejected),
            list.Count(t => t.Status == TaskItemStatus.Cancelled), open.Count(t => t.IsBlocked), open.Count(t => t.IsOverdue),
            live.Count(t => t.EstimateDays is null), totalDays, doneDays,
            live.Count == 0 ? 0 : (int)Math.Round(live.Count(Done) * 100.0 / live.Count),
            split, byType, byAssignee, byProject, open);
    }

    private async Task EnsureNoOverlapAsync(int? id, SaveSprintRequest r, CancellationToken ct)
    {
        var clash = await db.Sprints.Where(s => s.Id != id && s.Status != SprintStatus.Completed && s.StartDate <= r.EndDate && r.StartDate <= s.EndDate)
            .Select(s => s.Name).FirstOrDefaultAsync(ct);
        if (clash is not null) throw new ConflictException($"The dates overlap with sprint \"{clash}\".");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
