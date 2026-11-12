using System.Text;

namespace CompanyManagement.Application.Features.Tasks;

public enum AttachmentKind { Document, Image, Audio, Video }

/// <summary>
/// What may be uploaded to a task, how big it may be, and whether the bytes really are what the extension claims.
/// Media (images, audio, video) can be shown inline with a fixed safe content type; everything else is download-only.
/// </summary>
public static class AttachmentRules
{
    public const long MaxDocumentBytes = 10 * 1024 * 1024;
    public const long MaxImageBytes = 10 * 1024 * 1024;
    public const long MaxAudioBytes = 20 * 1024 * 1024;
    public const long MaxVideoBytes = 50 * 1024 * 1024;
    /// <summary>Largest request the upload endpoint accepts (the biggest kind).</summary>
    public const long MaxBytes = MaxVideoBytes;

    private record Rule(AttachmentKind Kind, string Mime);

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new(AttachmentKind.Document, "application/pdf"),
        [".doc"] = new(AttachmentKind.Document, "application/msword"),
        [".docx"] = new(AttachmentKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".xls"] = new(AttachmentKind.Document, "application/vnd.ms-excel"),
        [".xlsx"] = new(AttachmentKind.Document, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        [".ppt"] = new(AttachmentKind.Document, "application/vnd.ms-powerpoint"),
        [".pptx"] = new(AttachmentKind.Document, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        [".txt"] = new(AttachmentKind.Document, "text/plain"),
        [".csv"] = new(AttachmentKind.Document, "text/csv"),
        [".zip"] = new(AttachmentKind.Document, "application/zip"),

        [".png"] = new(AttachmentKind.Image, "image/png"),
        [".jpg"] = new(AttachmentKind.Image, "image/jpeg"),
        [".jpeg"] = new(AttachmentKind.Image, "image/jpeg"),
        [".gif"] = new(AttachmentKind.Image, "image/gif"),
        [".webp"] = new(AttachmentKind.Image, "image/webp"),
        [".heic"] = new(AttachmentKind.Image, "image/heic"),
        [".heif"] = new(AttachmentKind.Image, "image/heif"),

        [".webm"] = new(AttachmentKind.Audio, "audio/webm"),   // browser voice notes (Chrome, Edge, Firefox)
        [".ogg"] = new(AttachmentKind.Audio, "audio/ogg"),
        [".oga"] = new(AttachmentKind.Audio, "audio/ogg"),
        [".mp3"] = new(AttachmentKind.Audio, "audio/mpeg"),
        [".m4a"] = new(AttachmentKind.Audio, "audio/mp4"),     // Safari / iPhone voice notes
        [".wav"] = new(AttachmentKind.Audio, "audio/wav"),

        [".mp4"] = new(AttachmentKind.Video, "video/mp4"),
        [".mov"] = new(AttachmentKind.Video, "video/quicktime"),
    };

    public static IReadOnlyCollection<string> Extensions => Rules.Keys;

    public static bool IsAllowed(string extension) => Rules.ContainsKey(extension);

    /// <summary>Kind by extension; a .webm uploaded as video/* is a screen recording, otherwise a voice note.</summary>
    public static AttachmentKind KindOf(string extension, string? declaredContentType = null)
    {
        if (!Rules.TryGetValue(extension, out var r)) return AttachmentKind.Document;
        if (extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) && declaredContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            return AttachmentKind.Video;
        return r.Kind;
    }

    /// <summary>Kind of an already stored file (older uploads kept the client's content type, so the extension decides first).</summary>
    public static AttachmentKind KindOfStored(string fileName, string contentType)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            ? (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? AttachmentKind.Video : AttachmentKind.Audio)
            : KindOf(ext);
    }

    /// <summary>The content type we store and serve: decided by us, never taken from the client.</summary>
    public static string SafeContentType(string extension, AttachmentKind kind) =>
        extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) && kind == AttachmentKind.Video ? "video/webm"
        : Rules.TryGetValue(extension, out var r) ? r.Mime : "application/octet-stream";

    public static long MaxSize(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Audio => MaxAudioBytes,
        AttachmentKind.Video => MaxVideoBytes,
        AttachmentKind.Image => MaxImageBytes,
        _ => MaxDocumentBytes,
    };

    /// <summary>Images, audio and video can be displayed in the page; documents are always downloaded.</summary>
    public static bool IsInlineSafe(string contentType) =>
        contentType.StartsWith("image/", StringComparison.Ordinal) || contentType.StartsWith("audio/", StringComparison.Ordinal)
        || contentType.StartsWith("video/", StringComparison.Ordinal);

    /// <summary>
    /// Checks the first bytes of media files against their format. Stops e.g. an HTML page renamed to .png from being
    /// shown inline. Documents are download-only, so they are not sniffed.
    /// </summary>
    public static bool SignatureMatches(string extension, byte[] head)
    {
        bool Starts(params byte[] sig) => head.Length >= sig.Length && head.AsSpan(0, sig.Length).SequenceEqual(sig);
        bool Ascii(int offset, string s) => head.Length >= offset + s.Length && Encoding.ASCII.GetString(head, offset, s.Length) == s;
        bool IsoMedia() => Ascii(4, "ftyp"); // MP4 / MOV / M4A / HEIC containers

        return extension.ToLowerInvariant() switch
        {
            ".png" => Starts(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            ".jpg" or ".jpeg" => Starts(0xFF, 0xD8, 0xFF),
            ".gif" => Ascii(0, "GIF87a") || Ascii(0, "GIF89a"),
            ".webp" => Ascii(0, "RIFF") && Ascii(8, "WEBP"),
            ".heic" or ".heif" or ".mp4" or ".mov" or ".m4a" => IsoMedia(),
            ".webm" => Starts(0x1A, 0x45, 0xDF, 0xA3),
            ".ogg" or ".oga" => Ascii(0, "OggS"),
            ".wav" => Ascii(0, "RIFF") && Ascii(8, "WAVE"),
            ".mp3" => Ascii(0, "ID3") || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0),
            _ => true,
        };
    }
}
