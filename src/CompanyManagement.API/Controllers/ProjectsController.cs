using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Projects;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace CompanyManagement.API.Controllers;

/// <summary>
/// Projects. Anyone with <c>projects.create</c> can create one; admins, the creator and the BR Owners manage it
/// (checked in the service, per project).
/// </summary>
[ApiController]
[Route("api/projects")]
public class ProjectsController(IProjectService projects, IProjectDocumentService documents) : ControllerBase
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

    // ------------------------------------------------------------------ documents

    /// <summary>The project's documents (Discovery, BR, design, meetings, client files…).</summary>
    [HttpGet("{id:int}/documents")]
    public async Task<ActionResult<IReadOnlyList<ProjectDocumentDto>>> Documents(int id, CancellationToken ct) =>
        Ok(await documents.GetAllAsync(id, ct));

    /// <summary>
    /// Add a document (project members and managers). The text of PDF, Word, PowerPoint, Excel, text and Markdown files is
    /// extracted so Claude can read it. Sizes as for task files: documents and images 10 MB, audio 20 MB, video 50 MB.
    /// </summary>
    [HttpPost("{id:int}/documents")]
    [RequestSizeLimit(AttachmentRules.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = AttachmentRules.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<ProjectDocumentDto>> UploadDocument(int id, IFormFile file, [FromForm] string? title,
        [FromForm] ProjectDocumentCategory category = ProjectDocumentCategory.Other, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0) throw new BadRequestException("No file was uploaded.");
        await using var stream = file.OpenReadStream();
        return Ok(await documents.UploadAsync(id, title, category, file.FileName, file.ContentType, file.Length, stream, ct));
    }

    /// <summary>Write a Markdown document (e.g. meeting minutes).</summary>
    [HttpPost("{id:int}/documents/text")]
    public async Task<ActionResult<ProjectDocumentDto>> CreateTextDocument(int id, CreateTextDocumentRequest request, CancellationToken ct) =>
        Ok(await documents.CreateTextAsync(id, request, ct));

    /// <summary>The extracted text of a document, a page at a time.</summary>
    [HttpGet("{id:int}/documents/{documentId:int}/text")]
    public async Task<ActionResult<DocumentTextDto>> DocumentText(int id, int documentId, [FromQuery] int offset = 0,
        [FromQuery] int maxLength = 20_000, CancellationToken ct = default) =>
        Ok(await documents.GetTextAsync(id, documentId, offset, maxLength, ct));

    /// <summary>Download a document; with <c>inline=true</c> images, audio and video are served for preview.</summary>
    [HttpGet("{id:int}/documents/{documentId:int}")]
    public async Task<IActionResult> DownloadDocument(int id, int documentId, [FromQuery] bool inline = false, CancellationToken ct = default)
    {
        var (doc, content) = await documents.OpenAsync(id, documentId, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var ext = Path.GetExtension(doc.FileName);
        var mime = AttachmentRules.SafeContentType(ext, AttachmentRules.KindOfStored(doc.FileName, doc.ContentType));
        if (inline && AttachmentRules.IsAllowed(ext) && AttachmentRules.IsInlineSafe(mime))
        {
            Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            return File(content, mime, enableRangeProcessing: true);
        }
        return File(content, "application/octet-stream", doc.FileName);
    }

    [HttpDelete("{id:int}/documents/{documentId:int}")]
    public async Task<IActionResult> DeleteDocument(int id, int documentId, CancellationToken ct)
    {
        await documents.DeleteAsync(id, documentId, ct);
        return NoContent();
    }
}
