using CompanyManagement.API.Infrastructure;
using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyManagement.API.Controllers;

[ApiController]
[Route("api/tasks")]
public class TasksController(ITaskService tasks, IExportService export) : ControllerBase
{
    /// <summary>Task assigners, and the leads of the chosen project, can create tasks.</summary>
    [HttpPost]
    public async Task<ActionResult<TaskDetailsDto>> Create(CreateTaskRequest request, CancellationToken ct)
    {
        var t = await tasks.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = t.Task.Id }, t);
    }

    /// <summary>Tasks visible to the current user (all for admins, team tasks for managers, own tasks for employees).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<TaskDto>>> Get([FromQuery] TaskQuery query, CancellationToken ct) =>
        Ok(await tasks.GetPagedAsync(query, ct));

    [HttpGet("my-tasks")]
    public async Task<ActionResult<PagedResult<TaskDto>>> MyTasks([FromQuery] TaskQuery query, CancellationToken ct)
    {
        query.Mine = true;
        return Ok(await tasks.GetPagedAsync(query, ct));
    }

    /// <summary>Departments, teams and employees the current user may pick when creating/editing a task.</summary>
    [HttpGet("targets")]
    public async Task<ActionResult<TaskTargetsDto>> Targets([FromQuery] int? projectId, CancellationToken ct) =>
        Ok(await tasks.GetTargetsAsync(projectId, ct));

    /// <summary>Labels already used on visible tasks.</summary>
    [HttpGet("labels")]
    public async Task<ActionResult<IReadOnlyList<string>>> Labels(CancellationToken ct) => Ok(await tasks.GetLabelsAsync(ct));

    /// <summary>A task by its key, e.g. GA-123 (or #45).</summary>
    [HttpGet("by-key/{key}")]
    public async Task<ActionResult<TaskDetailsDto>> GetByKey(string key, CancellationToken ct) => Ok(await tasks.GetByKeyAsync(key, ct));

    [HttpGet("dashboard")]
    public async Task<ActionResult<TaskDashboardDto>> Dashboard([FromQuery] bool mine = false, CancellationToken ct = default) =>
        Ok(await tasks.GetDashboardAsync(mine, ct));

    [HttpGet("export")]
    [Authorize(Policy = Permissions.ReportsExport)]
    public async Task<IActionResult> Export([FromQuery] TaskQuery query, [FromQuery] string format = "xlsx", CancellationToken ct = default)
    {
        var rows = await tasks.GetAllForExportAsync(query, ct);
        return ExportResults.File(this, export, format, "tasks", "Tasks", null, rows, TaskColumns);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TaskDetailsDto>> GetById(int id, CancellationToken ct) => Ok(await tasks.GetAsync(id, ct));

    [HttpPut("{id:int}")]
    public async Task<ActionResult<TaskDetailsDto>> Update(int id, UpdateTaskRequest request, CancellationToken ct) =>
        Ok(await tasks.UpdateAsync(id, request, ct));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await tasks.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPatch("{id:int}/status")]
    public async Task<ActionResult<TaskDetailsDto>> ChangeStatus(int id, UpdateTaskStatusRequest request, CancellationToken ct) =>
        Ok(await tasks.ChangeStatusAsync(id, request, ct));

    [HttpPatch("{id:int}/progress")]
    public async Task<ActionResult<TaskDetailsDto>> UpdateProgress(int id, UpdateTaskProgressRequest request, CancellationToken ct) =>
        Ok(await tasks.UpdateProgressAsync(id, request, ct));

    /// <summary>Flag the task as blocked (with what it waits for) or clear it.</summary>
    [HttpPatch("{id:int}/blocked")]
    public async Task<ActionResult<TaskDetailsDto>> SetBlocked(int id, SetTaskBlockedRequest request, CancellationToken ct) =>
        Ok(await tasks.SetBlockedAsync(id, request, ct));

    /// <summary>Move the task to a sprint, or back to the backlog with <c>sprintId: null</c>.</summary>
    [HttpPatch("{id:int}/sprint")]
    public async Task<ActionResult<TaskDetailsDto>> SetSprint(int id, SetTaskSprintRequest request, CancellationToken ct) =>
        Ok(await tasks.SetSprintAsync(id, request, ct));

    /// <summary>This task waits for another task.</summary>
    [HttpPost("{id:int}/dependencies")]
    public async Task<ActionResult<TaskDetailsDto>> AddDependency(int id, AddDependencyRequest request, CancellationToken ct) =>
        Ok(await tasks.AddDependencyAsync(id, request, ct));

    [HttpDelete("{id:int}/dependencies/{blockedByTaskId:int}")]
    public async Task<ActionResult<TaskDetailsDto>> RemoveDependency(int id, int blockedByTaskId, CancellationToken ct) =>
        Ok(await tasks.RemoveDependencyAsync(id, blockedByTaskId, ct));

    [HttpPost("{id:int}/comments")]
    public async Task<ActionResult<TaskCommentDto>> AddComment(int id, AddCommentRequest request, CancellationToken ct) =>
        Ok(await tasks.AddCommentAsync(id, request, ct));

    /// <summary>Read receipt: the current user opened the task.</summary>
    [HttpPost("{id:int}/seen")]
    public async Task<IActionResult> Seen(int id, CancellationToken ct)
    {
        await tasks.MarkSeenAsync(id, ct);
        return NoContent();
    }

    [HttpGet("{id:int}/history")]
    public async Task<ActionResult<IReadOnlyList<TaskHistoryDto>>> History(int id, CancellationToken ct) => Ok(await tasks.GetHistoryAsync(id, ct));

    [HttpPost("{id:int}/attachments")]
    [RequestSizeLimit(TaskService.MaxAttachmentBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = TaskService.MaxAttachmentBytes + 64 * 1024)]
    public async Task<ActionResult<TaskAttachmentDto>> Upload(int id, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new BadRequestException("No file was uploaded.");
        await using var stream = file.OpenReadStream();
        return Ok(await tasks.AddAttachmentAsync(id, file.FileName, file.ContentType, file.Length, stream, ct));
    }

    [HttpGet("{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> Download(int id, int attachmentId, CancellationToken ct)
    {
        var (att, content) = await tasks.OpenAttachmentAsync(id, attachmentId, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Always download (never render inline) to avoid stored-XSS via uploaded HTML/SVG.
        return File(content, "application/octet-stream", att.FileName);
    }

    [HttpDelete("{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> DeleteAttachment(int id, int attachmentId, CancellationToken ct)
    {
        await tasks.DeleteAttachmentAsync(id, attachmentId, ct);
        return NoContent();
    }

    private static readonly IReadOnlyList<ExportColumn<TaskDto>> TaskColumns =
    [
        new("Key", t => t.Key),
        new("Title", t => t.Title),
        new("Type", t => t.Type.ToString()),
        new("Priority", t => t.Priority.ToString()),
        new("Status", t => t.Status.ToString()),
        new("Progress %", t => t.ProgressPercent),
        new("Assignees", t => string.Join(", ", t.Assignees.Select(a => a.FullName))),
        new("Project", t => t.ProjectName),
        new("Sprint", t => t.SprintName),
        new("Estimate (days)", t => t.EstimateDays),
        new("Blocked", t => t.IsBlocked),
        new("Story", t => t.StoryRef),
        new("Screen", t => t.ScreenRef),
        new("Labels", t => string.Join(", ", t.Labels)),
        new("Department", t => t.DepartmentName),
        new("Assigned by", t => t.AssignedByName),
        new("Start", t => t.StartDate),
        new("Due", t => t.DueDate),
        new("Completed", t => t.CompletionDate),
        new("Overdue", t => t.IsOverdue),
    ];
}
