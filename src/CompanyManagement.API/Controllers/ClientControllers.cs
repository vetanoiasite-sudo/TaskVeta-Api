using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Clients;
using CompanyManagement.Application.Features.Projects;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.API.Controllers;

/// <summary>Serves stored files safely: media inline with a server-chosen type and a sandbox CSP, everything else as a download.</summary>
internal static class FileResults
{
    public static IActionResult Serve(ControllerBase c, string fileName, string contentType, Stream content, bool inline)
    {
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var ext = Path.GetExtension(fileName);
        var mime = AttachmentRules.SafeContentType(ext, AttachmentRules.KindOfStored(fileName, contentType));
        if (inline && AttachmentRules.IsAllowed(ext) && AttachmentRules.IsInlineSafe(mime))
        {
            c.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            return c.File(content, mime, enableRangeProcessing: true);
        }
        return c.File(content, "application/octet-stream", fileName);
    }
}

/// <summary>Client accounts and the projects delivered to them.</summary>
[ApiController]
[Route("api/clients")]
[Authorize(Policy = Permissions.ClientsManage)]
public class ClientsController(IClientService clients) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ClientDto>>> Get([FromQuery] string? search, CancellationToken ct) =>
        Ok(await clients.GetAllAsync(search, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ClientDetailsDto>> GetById(int id, CancellationToken ct) => Ok(await clients.GetAsync(id, ct));

    /// <summary>Creates the client and its account. The response holds the temporary password (shown once).</summary>
    [HttpPost]
    public async Task<ActionResult<ClientCredentialsDto>> Create(SaveClientRequest request, CancellationToken ct) =>
        Ok(await clients.CreateAsync(request, ct));

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ClientDetailsDto>> Update(int id, SaveClientRequest request, CancellationToken ct) =>
        Ok(await clients.UpdateAsync(id, request, ct));

    /// <summary>Issues a new temporary password; the client must change it at the next sign-in.</summary>
    [HttpPost("{id:int}/reset-password")]
    public async Task<ActionResult<ClientCredentialsDto>> ResetPassword(int id, CancellationToken ct) => Ok(await clients.ResetPasswordAsync(id, ct));

    [HttpPut("{id:int}/active")]
    public async Task<ActionResult<ClientDetailsDto>> SetActive(int id, SetClientActiveRequest request, CancellationToken ct) =>
        Ok(await clients.SetActiveAsync(id, request.IsActive, ct));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await clients.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/projects")]
    public async Task<ActionResult<ClientDetailsDto>> LinkProject(int id, LinkClientProjectRequest request, CancellationToken ct) =>
        Ok(await clients.LinkProjectAsync(id, request, ct));

    [HttpDelete("{id:int}/projects/{clientProjectId:int}")]
    public async Task<ActionResult<ClientDetailsDto>> UnlinkProject(int id, int clientProjectId, CancellationToken ct) =>
        Ok(await clients.UnlinkProjectAsync(id, clientProjectId, ct));

    [HttpPost("{id:int}/projects/{clientProjectId:int}/notes")]
    public async Task<ActionResult<ClientDetailsDto>> AddNote(int id, int clientProjectId, SaveClientProjectNoteRequest request, CancellationToken ct) =>
        Ok(await clients.AddNoteAsync(id, clientProjectId, request, ct));

    [HttpPut("{id:int}/projects/{clientProjectId:int}/notes/{noteId:int}")]
    public async Task<ActionResult<ClientDetailsDto>> UpdateNote(int id, int clientProjectId, int noteId, SaveClientProjectNoteRequest request,
        CancellationToken ct) => Ok(await clients.UpdateNoteAsync(id, clientProjectId, noteId, request, ct));

    [HttpDelete("{id:int}/projects/{clientProjectId:int}/notes/{noteId:int}")]
    public async Task<ActionResult<ClientDetailsDto>> DeleteNote(int id, int clientProjectId, int noteId, CancellationToken ct) =>
        Ok(await clients.DeleteNoteAsync(id, clientProjectId, noteId, ct));
}

/// <summary>The support team's side of client tickets.</summary>
[ApiController]
[Route("api/support/tickets")]
[Authorize(Policy = Permissions.SupportManage)]
public class SupportTicketsController(ITicketService tickets, IClientService clients) : ControllerBase
{
    /// <summary>Client names for the ticket filters.</summary>
    [HttpGet("clients")]
    public async Task<ActionResult<IReadOnlyList<ClientLookupDto>>> Clients(CancellationToken ct) => Ok(await clients.LookupAsync(ct));

    [HttpGet]
    public async Task<ActionResult<PagedResult<TicketDto>>> Get([FromQuery] TicketQuery query, CancellationToken ct) =>
        Ok(await tickets.GetPagedAsync(query, ct));

    [HttpGet("counts")]
    public async Task<ActionResult<TicketCountsDto>> Counts(CancellationToken ct) => Ok(await tickets.GetCountsAsync(ct));

    [HttpGet("agents")]
    public async Task<ActionResult<IReadOnlyList<SupportAgentDto>>> Agents(CancellationToken ct) => Ok(await tickets.AgentsAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TicketDetailsDto>> GetById(int id, CancellationToken ct) => Ok(await tickets.GetAsync(id, ct));

    [HttpPut("{id:int}")]
    public async Task<ActionResult<TicketDetailsDto>> Update(int id, UpdateTicketRequest request, CancellationToken ct) =>
        Ok(await tickets.UpdateAsync(id, request, ct));

    /// <summary>Reply to the client, or write an internal note (<c>isInternal</c>) only the team sees.</summary>
    [HttpPost("{id:int}/messages")]
    public async Task<ActionResult<TicketDetailsDto>> AddMessage(int id, AddTicketMessageRequest request, CancellationToken ct) =>
        Ok(await tickets.AddMessageAsync(id, request, ct));

    [HttpPost("{id:int}/attachments")]
    [RequestSizeLimit(AttachmentRules.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = AttachmentRules.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<TicketAttachmentDto>> Upload(int id, IFormFile file, [FromForm] int? durationSeconds, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new BadRequestException("No file was uploaded.");
        await using var stream = file.OpenReadStream();
        return Ok(await tickets.AddAttachmentAsync(id, file.FileName, file.ContentType, file.Length, stream, durationSeconds, ct));
    }

    [HttpGet("{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> Download(int id, int attachmentId, [FromQuery] bool inline = false, CancellationToken ct = default)
    {
        var (att, content) = await tickets.OpenAttachmentAsync(id, attachmentId, ct);
        return FileResults.Serve(this, att.FileName, att.ContentType, content, inline);
    }

    [HttpDelete("{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> DeleteAttachment(int id, int attachmentId, CancellationToken ct)
    {
        await tickets.DeleteAttachmentAsync(id, attachmentId, ct);
        return NoContent();
    }
}

/// <summary>The client portal: a client's profile, projects and support tickets. Client accounts can call nothing else.</summary>
[ApiController]
[Route("api/portal")]
[Authorize(Roles = Roles.Client)]
public class PortalController(ITicketService tickets, IProjectService projects, IApplicationDbContext db, ICurrentUser currentUser) : ControllerBase
{
    public record PortalProfileDto(int ClientId, string Name, string? NameAr, string? ContactName, string Email, string? Phone);

    [HttpGet("me")]
    public async Task<ActionResult<PortalProfileDto>> Me(CancellationToken ct) =>
        Ok(await db.Clients.AsNoTracking().Where(c => c.UserId == currentUser.UserId)
               .Select(c => new PortalProfileDto(c.Id, c.Name, c.NameAr, c.ContactName, c.User.Email!, c.Phone)).FirstOrDefaultAsync(ct)
           ?? throw new ForbiddenException("This account is not linked to a client."));

    /// <summary>The client's projects with the notes shared with them (what was added or improved for them).</summary>
    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyList<ClientProjectDto>>> Projects(CancellationToken ct)
    {
        var clientId = await db.Clients.Where(c => c.UserId == currentUser.UserId).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct)
                       ?? throw new ForbiddenException("This account is not linked to a client.");
        return Ok(await ClientService.ProjectsOfAsync(db, clientId, includeInternalNotes: false, ct));
    }

    [HttpGet("projects/{projectId:int}/logo")]
    public async Task<IActionResult> ProjectLogo(int projectId, CancellationToken ct)
    {
        if (await projects.OpenLogoAsync(projectId, ct) is not { } logo) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(logo.Content, logo.ContentType);
    }

    [HttpGet("tickets")]
    public async Task<ActionResult<PagedResult<TicketDto>>> Tickets([FromQuery] TicketQuery query, CancellationToken ct) =>
        Ok(await tickets.GetPagedAsync(query, ct));

    [HttpGet("tickets/counts")]
    public async Task<ActionResult<TicketCountsDto>> Counts(CancellationToken ct) => Ok(await tickets.GetCountsAsync(ct));

    [HttpGet("tickets/{id:int}")]
    public async Task<ActionResult<TicketDetailsDto>> Ticket(int id, CancellationToken ct) => Ok(await tickets.GetAsync(id, ct));

    /// <summary>Open a ticket. Upload its files afterwards to <c>tickets/{id}/attachments</c>.</summary>
    [HttpPost("tickets")]
    public async Task<ActionResult<TicketDetailsDto>> Create(CreateTicketRequest request, CancellationToken ct) =>
        Ok(await tickets.CreateAsync(request, ct));

    [HttpPost("tickets/{id:int}/messages")]
    public async Task<ActionResult<TicketDetailsDto>> AddMessage(int id, AddTicketMessageRequest request, CancellationToken ct) =>
        Ok(await tickets.AddMessageAsync(id, request with { IsInternal = false }, ct));

    [HttpPost("tickets/{id:int}/close")]
    public async Task<ActionResult<TicketDetailsDto>> Close(int id, CancellationToken ct) => Ok(await tickets.CloseByClientAsync(id, ct));

    /// <summary>Upload a file, image or voice note (documents and images 10 MB, audio 20 MB, video 50 MB).</summary>
    [HttpPost("tickets/{id:int}/attachments")]
    [RequestSizeLimit(AttachmentRules.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = AttachmentRules.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<TicketAttachmentDto>> Upload(int id, IFormFile file, [FromForm] int? durationSeconds, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new BadRequestException("No file was uploaded.");
        await using var stream = file.OpenReadStream();
        return Ok(await tickets.AddAttachmentAsync(id, file.FileName, file.ContentType, file.Length, stream, durationSeconds, ct));
    }

    [HttpGet("tickets/{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> Download(int id, int attachmentId, [FromQuery] bool inline = false, CancellationToken ct = default)
    {
        var (att, content) = await tickets.OpenAttachmentAsync(id, attachmentId, ct);
        return FileResults.Serve(this, att.FileName, att.ContentType, content, inline);
    }

    [HttpDelete("tickets/{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> DeleteAttachment(int id, int attachmentId, CancellationToken ct)
    {
        await tickets.DeleteAttachmentAsync(id, attachmentId, ct);
        return NoContent();
    }
}

/// <summary>The company template library. Everyone reads; people with templates.manage upload, edit and delete (checked in the service).</summary>
[ApiController]
[Route("api/templates")]
public class TemplatesController(ITemplateService templates) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TemplateDto>>> Get(CancellationToken ct) => Ok(await templates.GetAllAsync(ct));

    [HttpPost]
    [RequestSizeLimit(AttachmentRules.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = AttachmentRules.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<TemplateDto>> Upload(IFormFile file, [FromForm] string? title, [FromForm] string? description,
        [FromForm] ProjectDocumentCategory category = ProjectDocumentCategory.Other, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0) throw new BadRequestException("No file was uploaded.");
        await using var stream = file.OpenReadStream();
        return Ok(await templates.UploadAsync(title, description, category, file.FileName, file.ContentType, file.Length, stream, ct));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<TemplateDto>> Update(int id, UpdateTemplateRequest request, CancellationToken ct) =>
        Ok(await templates.UpdateAsync(id, request, ct));

    [HttpPut("{id:int}/file")]
    [RequestSizeLimit(AttachmentRules.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = AttachmentRules.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<TemplateDto>> ReplaceFile(int id, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new BadRequestException("No file was uploaded.");
        await using var stream = file.OpenReadStream();
        return Ok(await templates.ReplaceFileAsync(id, file.FileName, file.ContentType, file.Length, stream, ct));
    }

    [HttpGet("{id:int}/file")]
    public async Task<IActionResult> Download(int id, [FromQuery] bool inline = false, CancellationToken ct = default)
    {
        var (template, content) = await templates.OpenAsync(id, ct);
        return FileResults.Serve(this, template.FileName, template.ContentType, content, inline);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await templates.DeleteAsync(id, ct);
        return NoContent();
    }
}
