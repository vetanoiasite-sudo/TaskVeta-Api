using CompanyManagement.Application.Features.Sprints;
using CompanyManagement.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyManagement.API.Controllers;

/// <summary>Company-wide sprints. Everyone can see them; <c>sprints.manage</c> is needed to change them.</summary>
[ApiController]
[Route("api/sprints")]
public class SprintsController(ISprintService sprints) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SprintDto>>> Get([FromQuery] bool includeCompleted = false, CancellationToken ct = default) =>
        Ok(await sprints.GetAllAsync(includeCompleted, ct));

    /// <summary>The active sprint, or 204 when none is active.</summary>
    [HttpGet("active")]
    public async Task<ActionResult<SprintDto>> Active(CancellationToken ct) =>
        await sprints.GetActiveAsync(ct) is { } s ? Ok(s) : NoContent();

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SprintDto>> GetById(int id, CancellationToken ct) => Ok(await sprints.GetAsync(id, ct));

    /// <summary>Progress, capacity split (new / improvements / bugs), workload per person and open tasks.</summary>
    [HttpGet("{id:int}/report")]
    public async Task<ActionResult<SprintReportDto>> Report(int id, CancellationToken ct) => Ok(await sprints.GetReportAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = Permissions.SprintsManage)]
    public async Task<ActionResult<SprintDto>> Create(SaveSprintRequest request, CancellationToken ct)
    {
        var s = await sprints.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = s.Id }, s);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = Permissions.SprintsManage)]
    public async Task<ActionResult<SprintDto>> Update(int id, SaveSprintRequest request, CancellationToken ct) =>
        Ok(await sprints.UpdateAsync(id, request, ct));

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permissions.SprintsManage)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await sprints.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/start")]
    [Authorize(Policy = Permissions.SprintsManage)]
    public async Task<ActionResult<SprintDto>> Start(int id, CancellationToken ct) => Ok(await sprints.StartAsync(id, ct));

    [HttpPost("{id:int}/complete")]
    [Authorize(Policy = Permissions.SprintsManage)]
    public async Task<ActionResult<SprintDto>> Complete(int id, CompleteSprintRequest request, CancellationToken ct) =>
        Ok(await sprints.CompleteAsync(id, request, ct));

    /// <summary>Plan several tasks into the sprint at once.</summary>
    [HttpPost("{id:int}/tasks")]
    [Authorize(Policy = Permissions.SprintsManage)]
    public async Task<ActionResult<SprintDto>> PlanTasks(int id, PlanTasksRequest request, CancellationToken ct) =>
        Ok(await sprints.PlanTasksAsync(id, request, ct));
}
