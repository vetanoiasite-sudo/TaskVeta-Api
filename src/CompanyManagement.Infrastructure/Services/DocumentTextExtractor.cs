using System.Text;
using ClosedXML.Excel;
using CompanyManagement.Application.Common;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace CompanyManagement.Infrastructure.Services;

/// <summary>
/// Extracts plain text from PDF (text layer only, not scanned images), Word (.docx), PowerPoint (.pptx), Excel (.xlsx),
/// and text / Markdown / CSV files. Old binary Office formats (.doc, .ppt, .xls) are not supported.
/// </summary>
public class DocumentTextExtractor(ILogger<DocumentTextExtractor> logger) : IDocumentTextExtractor
{
    private const int MaxSpreadsheetRows = 5000;
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".pptx", ".xlsx", ".txt", ".md", ".csv" };

    public bool Supports(string extension) => Supported.Contains(extension);

    public string? Extract(Stream content, string extension)
    {
        try
        {
            var text = extension.ToLowerInvariant() switch
            {
                ".pdf" => Pdf(content),
                ".docx" => Word(content),
                ".pptx" => PowerPoint(content),
                ".xlsx" => Excel(content),
                ".txt" or ".md" or ".csv" => PlainText(content),
                _ => null,
            };
            return string.IsNullOrWhiteSpace(text) ? null : Normalize(text);
        }
        catch (Exception ex)
        {
            // A damaged or password-protected file is still stored; it just can't be read by Claude.
            logger.LogWarning(ex, "Could not extract text from a {Extension} document", extension);
            return null;
        }
    }

    private static string Pdf(Stream content)
    {
        using var pdf = PdfDocument.Open(content);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            sb.Append("[Page ").Append(page.Number).AppendLine("]");
            sb.AppendLine(ContentOrderTextExtractor.GetText(page));
        }
        return sb.ToString();
    }

    private static string Word(Stream content)
    {
        using var doc = WordprocessingDocument.Open(content, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return "";
        var sb = new StringBuilder();
        foreach (var block in body.ChildElements)
        {
            switch (block)
            {
                case W.Paragraph p:
                    var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                    var line = p.InnerText;
                    // Keep headings visible so the structure (sections, requirements) survives.
                    if (style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true && int.TryParse(style[7..], out var level))
                        line = new string('#', Math.Clamp(level, 1, 6)) + " " + line;
                    sb.AppendLine(line);
                    break;
                case W.Table t:
                    foreach (var row in t.Elements<W.TableRow>())
                        sb.AppendLine("| " + string.Join(" | ", row.Elements<W.TableCell>().Select(c => c.InnerText.Trim())) + " |");
                    sb.AppendLine();
                    break;
            }
        }
        return sb.ToString();
    }

    private static string PowerPoint(Stream content)
    {
        using var doc = PresentationDocument.Open(content, false);
        var part = doc.PresentationPart;
        var ids = part?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>() ?? [];
        var sb = new StringBuilder();
        var n = 0;
        foreach (var id in ids)
        {
            n++;
            if (id.RelationshipId?.Value is not { } rel || part!.GetPartById(rel) is not SlidePart slide) continue;
            sb.Append("[Slide ").Append(n).AppendLine("]");
            foreach (var p in slide.Slide.Descendants<A.Paragraph>())
                if (p.InnerText.Trim() is { Length: > 0 } line) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static string Excel(Stream content)
    {
        using var wb = new XLWorkbook(content);
        var sb = new StringBuilder();
        var rows = 0;
        foreach (var ws in wb.Worksheets)
        {
            sb.Append("[Sheet ").Append(ws.Name).AppendLine("]");
            foreach (var row in ws.RowsUsed())
            {
                if (++rows > MaxSpreadsheetRows) { sb.AppendLine("… (more rows not included)"); return sb.ToString(); }
                sb.AppendLine(string.Join("\t", row.Cells(1, row.LastCellUsed()?.Address.ColumnNumber ?? 1).Select(c => c.GetFormattedString())));
            }
        }
        return sb.ToString();
    }

    private static string PlainText(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static string Normalize(string text) => text.Replace("\0", "").ReplaceLineEndings("\n").Trim();
}
