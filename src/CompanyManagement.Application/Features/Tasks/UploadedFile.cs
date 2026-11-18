using CompanyManagement.Application.Common;

namespace CompanyManagement.Application.Features.Tasks;

/// <summary>An upload that passed <see cref="AttachmentRules"/>: allowed type, size within the limit of its kind, real format checked.</summary>
public sealed record CheckedUpload(string Extension, AttachmentKind Kind, string SafeName, string ContentType, Stream Content);

public static class UploadedFile
{
    /// <summary>
    /// Validates an upload the same way task attachments are validated and returns a seekable stream positioned at 0.
    /// Throws <see cref="BadRequestException"/> with a readable message when the file is not acceptable.
    /// </summary>
    public static async Task<CheckedUpload> CheckAsync(string fileName, string? contentType, long size, Stream content, CancellationToken ct)
    {
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
        content.Position = 0;

        var safeName = Path.GetFileName(fileName).Replace("\"", "");
        if (safeName.Length > 255) safeName = safeName[^255..];
        return new CheckedUpload(ext, kind, safeName, AttachmentRules.SafeContentType(ext, kind), content);
    }
}
