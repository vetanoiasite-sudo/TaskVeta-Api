using CompanyManagement.Application.Features.Projects;
using Microsoft.AspNetCore.Mvc;

namespace CompanyManagement.API.Controllers;

/// <summary>
/// Projects. Anyone with <c>projects.create</c> can create one; admins, the creator and the BR Owners manage it
/// (checked in the service, per project).
/// </summary>
[ApiController]
[Route("api/projects")]
public class ProjectsController(IProjectService projects) : ControllerBase
{
    /// <summary>Projects the current user takes part in (all for admins), with counts over all of their tasks.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectDto>>> Get(CancellationToken ct) => Ok(await projects.GetAllAsync(ct));

    [HttpGet("lookup")]
    public async Task<ActionResult<IReadOnlyList<ProjectLookupDto>>> Lookup(CancellationToken ct) => Ok(await projects.LookupAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProjectDto>> GetById(int id, CancellationToken ct) => Ok(await projects.GetAsync(id, ct));

    /// <summary>The project and all of its tasks for the timeline view (only for users who take part in it).</summary>
    [HttpGet("{id:int}/timeline")]
    public async Task<ActionResult<ProjectTimelineDto>> Timeline(int id, CancellationToken ct) => Ok(await projects.GetTimelineAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<ProjectDto>> Create(SaveProjectRequest request, CancellationToken ct)
    {
        var p = await projects.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = p.Id }, p);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ProjectDto>> Update(int id, SaveProjectRequest request, CancellationToken ct) =>
        Ok(await projects.UpdateAsync(id, request, ct));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await projects.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Employees that can be added to the project (for people who manage it).</summary>
    [HttpGet("{id:int}/member-candidates")]
    public async Task<ActionResult<IReadOnlyList<ProjectMemberCandidateDto>>> MemberCandidates(int id, CancellationToken ct) =>
        Ok(await projects.MemberCandidatesAsync(id, ct));

    [HttpPost("{id:int}/members")]
    public async Task<ActionResult<ProjectDto>> AddMember(int id, AddProjectMemberRequest request, CancellationToken ct) =>
        Ok(await projects.AddMemberAsync(id, request, ct));

    [HttpDelete("{id:int}/members/{memberId:int}")]
    public async Task<ActionResult<ProjectDto>> RemoveMember(int id, int memberId, CancellationToken ct) =>
        Ok(await projects.RemoveMemberAsync(id, memberId, ct));
}
