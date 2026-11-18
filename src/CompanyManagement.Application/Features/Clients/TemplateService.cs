using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Projects;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Clients;

public record TemplateDto(int Id, string Title, string? Description, ProjectDocumentCategory Category, string FileName, string ContentType,
    long SizeBytes, AttachmentKind Kind, int UploadedByUserId, string UploadedByName, DateTime CreatedAt, DateTime UpdatedAt);

public record UpdateTemplateRequest(string Title, string? Description, ProjectDocumentCategory Category);

public class UpdateTemplateRequestValidator : AbstractValidator<UpdateTemplateRequest>
{
    public UpdateTemplateRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Category).IsInEnum();
    }
}

public interface ITemplateService
{
    Task<IReadOnlyList<TemplateDto>> GetAllAsync(CancellationToken ct = default);
    Task<TemplateDto> UploadAsync(string? title, string? description, ProjectDocumentCategory category, string fileName, string? contentType,
        long size, Stream content, CancellationToken ct = default);
    Task<TemplateDto> UpdateAsync(int id, UpdateTemplateRequest request, CancellationToken ct = default);
    /// <summary>Replaces the template's file and keeps its title, category and description.</summary>
    Task<TemplateDto> ReplaceFileAsync(int id, string fileName, string? contentType, long size, Stream content, CancellationToken ct = default);
    Task<(CompanyTemplate Template, Stream Content)> OpenAsync(int id, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>The company's template library: every employee can download; people with templates.manage upload, edit and delete.</summary>
public class TemplateService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IFileStorage storage,
    IAuditService audit) : ITemplateService
{
    public const int MaxTemplates = 1000;

    public async Task<IReadOnlyList<TemplateDto>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await db.CompanyTemplates.AsNoTracking().OrderByDescending(t => t.UpdatedAt)
            .Select(t => new { t.Id, t.Title, t.Description, t.Category, t.FileName, t.ContentType, t.SizeBytes, t.UploadedByUserId, t.UploadedBy.FullName, t.CreatedAt, t.UpdatedAt })
            .ToListAsync(ct);
        return rows.OrderBy(t => ProjectDocumentCategories.Rank(t.Category)).ThenBy(t => t.Title)
            .Select(t => new TemplateDto(t.Id, t.Title, t.Description, t.Category, t.FileName, t.ContentType, t.SizeBytes,
                AttachmentRules.KindOfStored(t.FileName, t.ContentType), t.UploadedByUserId, t.FullName, t.CreatedAt, t.UpdatedAt)).ToList();
    }

    public async Task<TemplateDto> UploadAsync(string? title, string? description, ProjectDocumentCategory category, string fileName,
        string? contentType, long size, Stream content, CancellationToken ct = default)
    {
        EnsureCanManage();
        if (!Enum.IsDefined(category)) throw new BadRequestException("Unknown template category.");
        if (title?.Length > 200) throw new BadRequestException("The title is too long (200 characters at most).");
        if (description?.Length > 1000) throw new BadRequestException("The description is too long (1000 characters at most).");
        if (await db.CompanyTemplates.CountAsync(ct) >= MaxTemplates) throw new BadRequestException($"The library is full ({MaxTemplates} templates).");
        var file = await UploadedFile.CheckAsync(fileName, contentType, size, content, ct);
        var stored = await storage.SaveAsync(file.Content, file.Extension, ct);
        var template = new CompanyTemplate
        {
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.SafeName) : title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(), Category = category,
            FileName = file.SafeName, StoredFileName = stored, ContentType = file.ContentType, SizeBytes = size,
            UploadedByUserId = currentUser.UserId!.Value,
        };
        db.CompanyTemplates.Add(template);
        try { await db.SaveChangesAsync(ct); }
        catch { storage.Delete(stored); throw; }
        audit.Log("Upload", nameof(CompanyTemplate), template.Id, newValues: new { template.Title, Category = category.ToString(), template.FileName });
        await db.SaveChangesAsync(ct);
        return await GetAsync(template.Id, ct);
    }

    public async Task<TemplateDto> UpdateAsync(int id, UpdateTemplateRequest r, CancellationToken ct = default)
    {
        EnsureCanManage();
        var template = await db.CompanyTemplates.FindAsync([id], ct) ?? throw new NotFoundException("Template", id);
        template.Title = r.Title.Trim();
        template.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        template.Category = r.Category;
        audit.Log("Update", nameof(CompanyTemplate), id, newValues: r);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<TemplateDto> ReplaceFileAsync(int id, string fileName, string? contentType, long size, Stream content, CancellationToken ct = default)
    {
        EnsureCanManage();
        var template = await db.CompanyTemplates.FindAsync([id], ct) ?? throw new NotFoundException("Template", id);
        var file = await UploadedFile.CheckAsync(fileName, contentType, size, content, ct);
        var stored = await storage.SaveAsync(file.Content, file.Extension, ct);
        var previous = template.StoredFileName;
        template.FileName = file.SafeName;
        template.StoredFileName = stored;
        template.ContentType = file.ContentType;
        template.SizeBytes = size;
        template.UploadedByUserId = currentUser.UserId!.Value;
        audit.Log("ReplaceFile", nameof(CompanyTemplate), id, newValues: new { template.FileName });
        try { await db.SaveChangesAsync(ct); }
        catch { storage.Delete(stored); throw; }
        storage.Delete(previous);
        return await GetAsync(id, ct);
    }

    public async Task<(CompanyTemplate Template, Stream Content)> OpenAsync(int id, CancellationToken ct = default)
    {
        var template = await db.CompanyTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Template", id);
        return (template, storage.OpenRead(template.StoredFileName));
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        EnsureCanManage();
        var template = await db.CompanyTemplates.FindAsync([id], ct) ?? throw new NotFoundException("Template", id);
        db.CompanyTemplates.Remove(template);
        audit.Log("Delete", nameof(CompanyTemplate), id, new { template.Title, template.FileName });
        await db.SaveChangesAsync(ct);
        storage.Delete(template.StoredFileName);
    }

    private async Task<TemplateDto> GetAsync(int id, CancellationToken ct) =>
        (await GetAllAsync(ct)).FirstOrDefault(t => t.Id == id) ?? throw new NotFoundException("Template", id);

    private void EnsureCanManage()
    {
        if (!currentUser.HasPermission(Permissions.TemplatesManage)) throw new ForbiddenException("You cannot manage templates.");
    }
}
