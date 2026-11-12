using System.Text;
using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Projects;

/// <param name="HasText">The text was extracted, so Claude can read and search it.</param>
/// <param name="Source">"Claude" when Claude wrote the document; null when it was uploaded in the app.</param>
public record ProjectDocumentDto(int Id, int ProjectId, string Title, ProjectDocumentCategory Category, string FileName, string ContentType,
    long SizeBytes, AttachmentKind Kind, int UploadedByUserId, string UploadedByName, DateTime CreatedAt, bool HasText, int TextLength,
    string? Source);

/// <summary>A page of a document's text. Read the next page from <c>Offset + Text.Length</c> while <c>HasMore</c>.</summary>
public record DocumentTextDto(int Id, string Title, string FileName, ProjectDocumentCategory Category, int TotalLength, int Offset,
    string Text, bool HasMore);

public record DocumentSearchHitDto(int DocumentId, int ProjectId, string ProjectCode, string Title, ProjectDocumentCategory Category,
    int Position, string Snippet);

/// <summary>A document written as text (Markdown), e.g. meeting minutes or a BR draft prepared with Claude.</summary>
public record CreateTextDocumentRequest(string Title, ProjectDocumentCategory Category, string Content);

public class CreateTextDocumentRequestValidator : AbstractValidator<CreateTextDocumentRequest>
{
    public CreateTextDocumentRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Content).NotEmpty().MaximumLength(ProjectDocumentService.MaxTextLength);
    }
}

public interface IProjectDocumentService
{
    Task<IReadOnlyList<ProjectDocumentDto>> GetAllAsync(int projectId, CancellationToken ct = default);
    Task<ProjectDocumentDto> UploadAsync(int projectId, string? title, ProjectDocumentCategory category, string fileName, string? contentType,
        long size, Stream content, CancellationToken ct = default);
    Task<ProjectDocumentDto> CreateTextAsync(int projectId, CreateTextDocumentRequest request, CancellationToken ct = default);
    Task<(ProjectDocument Document, Stream Content)> OpenAsync(int projectId, int documentId, CancellationToken ct = default);
    Task<DocumentTextDto> GetTextAsync(int projectId, int documentId, int offset = 0, int maxLength = 20_000, CancellationToken ct = default);
    /// <summary>Finds text in the documents of the projects the user can see (one project, or all of them).</summary>
    Task<IReadOnlyList<DocumentSearchHitDto>> SearchAsync(string query, int? projectId, CancellationToken ct = default);
    Task DeleteAsync(int projectId, int documentId, CancellationToken ct = default);
}

/// <summary>
/// Project documents. Everyone who can see the project can read them; project members and the people who manage
/// the project can add them; the uploader and the project's managers can delete them.
/// </summary>
public class ProjectDocumentService(
    IApplicationDbContext db,
    ITaskService tasks,
    IProjectAccess access,
    ICurrentUser currentUser,
    IFileStorage storage,
    IDocumentTextExtractor extractor,
    IAuditService audit) : IProjectDocumentService
{
    public const int MaxTextLength = 2_000_000;
    public const int MaxDocumentsPerProject = 500;

    public async Task<IReadOnlyList<ProjectDocumentDto>> GetAllAsync(int projectId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(projectId, ct);
        var docs = await Query(db.ProjectDocuments.Where(d => d.ProjectId == projectId).OrderByDescending(d => d.CreatedAt)).ToListAsync(ct);
        // Template order (Discovery, BR, UI/UX, Backend, Frontend, Testing, …), newest first inside a category.
        return docs.OrderBy(d => ProjectDocumentCategories.Rank(d.Category)).Select(ToDto).ToList();
    }

    public async Task<ProjectDocumentDto> UploadAsync(int projectId, string? title, ProjectDocumentCategory category, string fileName,
        string? contentType, long size, Stream content, CancellationToken ct = default)
    {
        await EnsureCanAddAsync(projectId, ct);
        if (!Enum.IsDefined(category)) throw new BadRequestException("Unknown document category.");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AttachmentRules.IsAllowed(ext)) throw new BadRequestException($"File type '{ext}' is not allowed.");
        var kind = AttachmentRules.KindOf(ext, contentType);
        var max = AttachmentRules.MaxSize(kind);
        if (size <= 0 || size > max) throw new BadRequestException($"{kind} files must be between 1 byte and {max / (1024 * 1024)} MB.");

        if (!content.CanSeek)
        {
            var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            content = copy;
        }
        content.Position = 0;
        var head = new byte[16];
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        if (!AttachmentRules.SignatureMatches(ext, head[..read]))
            throw new BadRequestException($"The file content does not match its '{ext}' extension.");

        string? text = null;
        if (extractor.Supports(ext))
        {
            content.Position = 0;
            text = Truncate(extractor.Extract(content, ext));
        }
        content.Position = 0;
        var safeName = Path.GetFileName(fileName).Replace("\"", "");
        if (safeName.Length > 255) safeName = safeName[^255..];
        var docTitle = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(safeName) : title.Trim();
        return await SaveAsync(projectId, docTitle, category, safeName, AttachmentRules.SafeContentType(ext, kind), size, content, ext, text, ct);
    }

    public async Task<ProjectDocumentDto> CreateTextAsync(int projectId, CreateTextDocumentRequest r, CancellationToken ct = default)
    {
        await EnsureCanAddAsync(projectId, ct);
        var bytes = Encoding.UTF8.GetBytes(r.Content);
        if (bytes.Length > AttachmentRules.MaxDocumentBytes) throw new BadRequestException("The document is too long (10 MB at most).");
        var title = r.Title.Trim();
        using var stream = new MemoryStream(bytes);
        return await SaveAsync(projectId, title, r.Category, Slug(title) + ".md", "text/markdown", bytes.Length, stream, ".md", r.Content, ct);
    }

    public async Task<(ProjectDocument Document, Stream Content)> OpenAsync(int projectId, int documentId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(projectId, ct);
        var doc = await db.ProjectDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.ProjectId == projectId, ct)
                  ?? throw new NotFoundException("Document", documentId);
        return (doc, storage.OpenRead(doc.StoredFileName));
    }

    public async Task<DocumentTextDto> GetTextAsync(int projectId, int documentId, int offset = 0, int maxLength = 20_000, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(projectId, ct);
        var doc = await db.ProjectDocuments.AsNoTracking().Where(d => d.Id == documentId && d.ProjectId == projectId)
                      .Select(d => new { d.Id, d.Title, d.FileName, d.Category, d.ExtractedText }).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Document", documentId);
        if (doc.ExtractedText is null)
            throw new BadRequestException($"\"{doc.Title}\" has no readable text (images, audio, video and scanned PDFs cannot be read as text).");
        var text = doc.ExtractedText;
        offset = Math.Clamp(offset, 0, text.Length);
        maxLength = Math.Clamp(maxLength, 500, 50_000);
        var page = text.Substring(offset, Math.Min(maxLength, text.Length - offset));
        return new DocumentTextDto(doc.Id, doc.Title, doc.FileName, doc.Category, text.Length, offset, page, offset + page.Length < text.Length);
    }

    public async Task<IReadOnlyList<DocumentSearchHitDto>> SearchAsync(string query, int? projectId, CancellationToken ct = default)
    {
        query = query?.Trim() ?? "";
        if (query.Length < 2) throw new BadRequestException("Search for at least 2 characters.");
        if (query.Length > 200) query = query[..200];
        var q = db.ProjectDocuments.AsNoTracking().Where(d => d.ExtractedText != null);
        if (projectId is int pid)
        {
            await EnsureVisibleAsync(pid, ct);
            q = q.Where(d => d.ProjectId == pid);
        }
        else if (await tasks.AccessibleProjectIdsAsync(ct) is { } allowed)
            q = q.Where(d => allowed.Contains(d.ProjectId));

        var lower = query.ToLower();
        var docs = await q.Where(d => d.ExtractedText!.ToLower().Contains(lower) || d.Title.ToLower().Contains(lower))
            .OrderByDescending(d => d.CreatedAt).Take(20)
            .Select(d => new { d.Id, d.ProjectId, d.Project.Code, d.Title, d.Category, d.ExtractedText }).ToListAsync(ct);

        var hits = new List<DocumentSearchHitDto>();
        foreach (var d in docs)
        {
            var text = d.ExtractedText!;
            var start = 0;
            var found = 0;
            while (found < 3 && (start = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var from = Math.Max(0, start - 150);
                var to = Math.Min(text.Length, start + query.Length + 150);
                hits.Add(new DocumentSearchHitDto(d.Id, d.ProjectId, d.Code, d.Title, d.Category, start,
                    (from > 0 ? "…" : "") + text[from..to].ReplaceLineEndings(" ") + (to < text.Length ? "…" : "")));
                start += query.Length;
                found++;
            }
            if (found == 0) hits.Add(new DocumentSearchHitDto(d.Id, d.ProjectId, d.Code, d.Title, d.Category, 0, "(the title matches)"));
        }
        return hits;
    }

    public async Task DeleteAsync(int projectId, int documentId, CancellationToken ct = default)
    {
        await EnsureVisibleAsync(projectId, ct);
        var doc = await db.ProjectDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.ProjectId == projectId, ct)
                  ?? throw new NotFoundException("Document", documentId);
        if (doc.UploadedByUserId != currentUser.UserId && !await access.CanManageAsync(projectId, ct))
            throw new ForbiddenException("Only the uploader and the project's managers can delete this document.");
        db.ProjectDocuments.Remove(doc);
        audit.Log("DeleteDocument", nameof(Project), projectId, new { doc.Id, doc.Title, doc.FileName });
        await db.SaveChangesAsync(ct);
        storage.Delete(doc.StoredFileName);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ProjectDocumentDto> SaveAsync(int projectId, string title, ProjectDocumentCategory category, string fileName,
        string contentType, long size, Stream content, string ext, string? text, CancellationToken ct)
    {
        if (await db.ProjectDocuments.CountAsync(d => d.ProjectId == projectId, ct) >= MaxDocumentsPerProject)
            throw new BadRequestException($"A project can hold up to {MaxDocumentsPerProject} documents.");
        var stored = await storage.SaveAsync(content, ext, ct);
        var doc = new ProjectDocument
        {
            ProjectId = projectId, Title = title.Length > 200 ? title[..200] : title, Category = category, FileName = fileName,
            StoredFileName = stored, ContentType = contentType, SizeBytes = size, UploadedByUserId = currentUser.UserId!.Value,
            ExtractedText = string.IsNullOrWhiteSpace(text) ? null : text,
        };
        db.ProjectDocuments.Add(doc);
        audit.Log("AddDocument", nameof(Project), projectId, newValues: new { doc.Title, doc.FileName, Category = category.ToString() });
        try { await db.SaveChangesAsync(ct); }
        catch { storage.Delete(stored); throw; }
        return ToDto(await Query(db.ProjectDocuments.Where(d => d.Id == doc.Id)).FirstAsync(ct));
    }

    /// <summary>Filter and order <paramref name="source"/> first: EF can't translate filters on the projected record.</summary>
    private static IQueryable<DocRow> Query(IQueryable<ProjectDocument> source) => source.AsNoTracking().Select(d => new DocRow(d.Id, d.ProjectId,
        d.Title, d.Category, d.FileName, d.ContentType, d.SizeBytes, d.UploadedByUserId, d.UploadedBy.FullName, d.CreatedAt,
        d.ExtractedText == null ? 0 : d.ExtractedText.Length, d.Source));

    private sealed record DocRow(int Id, int ProjectId, string Title, ProjectDocumentCategory Category, string FileName, string ContentType,
        long SizeBytes, int UploadedByUserId, string UploadedByName, DateTime CreatedAt, int TextLength, string? Source);

    private static ProjectDocumentDto ToDto(DocRow d) => new(d.Id, d.ProjectId, d.Title, d.Category, d.FileName, d.ContentType, d.SizeBytes,
        AttachmentRules.KindOfStored(d.FileName, d.ContentType), d.UploadedByUserId, d.UploadedByName, d.CreatedAt, d.TextLength > 0,
        d.TextLength, d.Source);

    private async Task EnsureVisibleAsync(int projectId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) throw new NotFoundException("Project", projectId);
        if (await tasks.AccessibleProjectIdsAsync(ct) is { } allowed && !allowed.Contains(projectId)) throw new NotFoundException("Project", projectId);
    }

    private async Task EnsureCanAddAsync(int projectId, CancellationToken ct)
    {
        await EnsureVisibleAsync(projectId, ct);
        if ((await access.MyRolesAsync(ct)).ContainsKey(projectId) || await access.CanManageAsync(projectId, ct)) return;
        throw new ForbiddenException("Only project members and the people who manage the project can add documents.");
    }

    private static string? Truncate(string? text) =>
        text is null ? null : text.Length > MaxTextLength ? text[..MaxTextLength] : text;

    private static string Slug(string title)
    {
        var chars = title.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0) slug = "document";
        return slug.Length > 80 ? slug[..80] : slug;
    }
}
