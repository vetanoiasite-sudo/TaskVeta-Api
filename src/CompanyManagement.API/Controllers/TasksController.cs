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
    [HttpPost]
    [Authorize(Policy = Permissions.TasksAssign)]
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
    [Authorize(Policy = Permissions.TasksAssign)]
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

    [HttpPost("{id:int}/comments")]
    public async Task<ActionResult<TaskCommentDto>> AddComment(int id, AddCommentRequest request, CancellationToken ct) =>
        Ok(await tasks.AddCommentAsync(id, request, ct));

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
        new("Id", t => t.Id),
        new("Title", t => t.Title),
        new("Priority", t => t.Priority.ToString()),
        new("Status", t => t.Status.ToString()),
        new("Progress %", t => t.ProgressPercent),
        new("Assignees", t => string.Join(", ", t.Assignees.Select(a => a.FullName))),
        new("Department", t => t.DepartmentName),
        new("Assigned by", t => t.AssignedByName),
        new("Start", t => t.StartDate),
        new("Due", t => t.DueDate),
        new("Completed", t => t.CompletionDate),
        new("Overdue", t => t.IsOverdue),
    ];
}
