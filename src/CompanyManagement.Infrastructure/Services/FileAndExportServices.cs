using System.Globalization;
using ClosedXML.Excel;
using CompanyManagement.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CompanyManagement.Infrastructure.Services;

/// <summary>Stores uploads outside the web root under random names; files are only served through authorized endpoints.</summary>
public class LocalFileStorage : IFileStorage
{
    private readonly string _root;

    public LocalFileStorage(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Storage:UploadsPath"];
        _root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "App_Data", "uploads")
            : Path.GetFullPath(configured);
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default)
    {
        var name = $"{Guid.NewGuid():N}{extension}";
        var path = Resolve(name);
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(fs, ct);
        return name;
    }

    public Stream OpenRead(string storedFileName)
    {
        var path = Resolve(storedFileName);
        if (!File.Exists(path)) throw new NotFoundException("File", storedFileName);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    }

    public void Delete(string storedFileName)
    {
        var path = Resolve(storedFileName);
        if (File.Exists(path)) File.Delete(path);
    }

    private string Resolve(string name)
    {
        var safe = Path.GetFileName(name);
        var full = Path.GetFullPath(Path.Combine(_root, safe));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) throw new BadRequestException("Invalid file name.");
        return full;
    }
}

public class ExportService : IExportService
{
    static ExportService()
    {
        // QuestPDF Community license: free for organizations with < $1M annual revenue. See README.
        QuestPDF.Settings.License = LicenseType.Community;
        // Lato ships with QuestPDF. System fonts are also discovered so that, for example, an Arabic-capable font
        // installed on the server (or copied next to the binaries) can be used.
        QuestPDF.Settings.UseSystemFonts = true;
    }

    public byte[] ToExcel<T>(string sheetTitle, IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SafeSheetName(sheetTitle));
        for (var c = 0; c < columns.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = columns[c].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F3A5F");
            cell.Style.Font.FontColor = XLColor.White;
        }
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < columns.Count; c++) SetCell(ws.Cell(r, c + 1), columns[c].Value(row));
            r++;
        }
        var range = ws.Range(1, 1, Math.Max(1, r - 1), columns.Count);
        range.SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(r, 500), 8, 60);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public byte[] ToPdf<T>(string title, string? subtitle, IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns)
    {
        var data = rows.Select(row => columns.Select(c => Format(c.Value(row))).ToArray()).ToList();
        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(columns.Count > 6 ? PageSizes.A4.Landscape() : PageSizes.A4);
                page.Margin(24);
                page.DefaultTextStyle(t => t.FontSize(8.5f).FontFamily(Fonts.Lato));
                page.Header().Column(col =>
                {
                    col.Item().Text(title).FontSize(15).Bold().FontColor("#1F3A5F");
                    if (!string.IsNullOrEmpty(subtitle)) col.Item().Text(subtitle).FontSize(9).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingBottom(6).Text($"Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · {data.Count} rows").FontSize(8).FontColor(Colors.Grey.Medium);
                });
                page.Content().Table(table =>
                {
                    table.ColumnsDefinition(cd => { foreach (var _ in columns) cd.RelativeColumn(); });
                    table.Header(h =>
                    {
                        foreach (var c in columns)
                            h.Cell().Background("#1F3A5F").Padding(4).Text(c.Header).FontColor(Colors.White).Bold();
                    });
                    var i = 0;
                    foreach (var row in data)
                    {
                        string bg = i++ % 2 == 0 ? "#FFFFFF" : "#F3F6FA";
                        foreach (var value in row)
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(value);
                    }
                });
                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    private static void SetCell(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null: cell.Value = Blank.Value; break;
            case int i: cell.Value = i; break;
            case long l: cell.Value = l; break;
            case decimal d: cell.Value = d; break;
            case double db: cell.Value = db; break;
            case bool b: cell.Value = b ? "Yes" : "No"; break;
            case DateTime dt: cell.Value = dt; cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm"; break;
            case DateTimeOffset dto: cell.Value = dto.DateTime; cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm"; break;
            case DateOnly d0: cell.Value = d0.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "yyyy-mm-dd"; break;
            default: cell.Value = value.ToString(); break;
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "Yes" : "No",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
        double db => db.ToString("0.##", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string SafeSheetName(string name)
    {
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return clean.Length > 31 ? clean[..31] : clean.Length == 0 ? "Sheet1" : clean;
    }
}

