using System.Text;
using ClosedXML.Excel;
using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Claude;
using CompanyManagement.Application.Features.Projects;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using CompanyManagement.Infrastructure.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Fluent;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace CompanyManagement.Tests;

/// <summary>Claude access: personal keys, project documents (with text extraction), PR links and the "via Claude" stamp.</summary>
public partial class DevProcessTests
{
    // ------------------------------------------------------------------ personal keys

    [Fact]
    public async Task Personal_key_is_shown_once_and_only_its_hash_is_stored()
    {
        var w = new World();
        var created = await w.As(Dev1, Roles.Employee).Keys.CreateAsync(new CreatePersonalKeyRequest("Laptop – Claude Code", 90));

        Assert.StartsWith("tvk_", created.Secret);
        Assert.True(created.Secret.Length > 40);
        Assert.Equal(created.Secret[..8] + "…", created.Key.Hint);
        Assert.Equal(w.Time.GetUtcNow().UtcDateTime.AddDays(90), created.Key.ExpiresAt);
        using var db = w.NewContext();
        var row = await db.PersonalAccessTokens.SingleAsync();
        Assert.NotEqual(created.Secret, row.TokenHash);
        Assert.DoesNotContain(created.Secret, row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length); // SHA-256 hex
    }

    [Fact]
    public async Task Personal_keys_are_private_and_can_be_revoked()
    {
        var w = new World();
        var key = (await w.As(Dev1, Roles.Employee).Keys.CreateAsync(new CreatePersonalKeyRequest("Desktop", null))).Key;

        Assert.Empty(await w.As(Dev2, Roles.Employee).Keys.GetMineAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => w.As(Dev2, Roles.Employee).Keys.RevokeAsync(key.Id));

        await w.As(Dev1, Roles.Employee).Keys.RevokeAsync(key.Id);
        var mine = Assert.Single(await w.As(Dev1, Roles.Employee).Keys.GetMineAsync());
        Assert.False(mine.IsActive);
        Assert.NotNull(mine.RevokedAt);
    }

    [Fact]
    public async Task Personal_keys_are_limited_and_cannot_be_created_through_Claude()
    {
        var w = new World();
        for (var i = 0; i < PersonalKeyService.MaxActiveKeys; i++)
            await w.As(Dev1, Roles.Employee).Keys.CreateAsync(new CreatePersonalKeyRequest($"k{i}", null));
        await Assert.ThrowsAsync<BadRequestException>(() => w.As(Dev1, Roles.Employee).Keys.CreateAsync(new CreatePersonalKeyRequest("one more", null)));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.ViaClaude(Dev2, Roles.Employee).Keys.CreateAsync(new CreatePersonalKeyRequest("x", null)));
    }

    [Fact]
    public void Expired_keys_are_not_active()
    {
        var now = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(new PersonalAccessToken { ExpiresAt = now.AddMinutes(1) }.IsActive(now));
        Assert.False(new PersonalAccessToken { ExpiresAt = now }.IsActive(now));
        Assert.False(new PersonalAccessToken { RevokedAt = now.AddDays(-1) }.IsActive(now));
        Assert.True(new PersonalAccessToken().IsActive(now));
    }

    // ------------------------------------------------------------------ project documents

    private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Project_members_add_documents_and_their_text_is_readable()
    {
        var w = new World();
        const string br = "# Garas Auto BR\n\n## US-AUTH-01 Login\nAs a driver I sign in with my phone number and an OTP.";
        var doc = await w.As(Dev1, Roles.Employee).Documents.UploadAsync(GA, "BR v1", ProjectDocumentCategory.BusinessRequirements,
            "garas-br.md", "text/markdown", Encoding.UTF8.GetByteCount(br), Text(br));

        Assert.Equal("BR v1", doc.Title);
        Assert.True(doc.HasText);
        Assert.Equal("dev1", doc.UploadedByName);
        Assert.Null(doc.Source);

        var text = await w.As(BrOwner, Roles.Employee).Documents.GetTextAsync(GA, doc.Id);
        Assert.Equal(br, text.Text);
        Assert.False(text.HasMore);
        Assert.Single(await w.AsAdmin().Documents.GetAllAsync(GA));
    }

    [Fact]
    public async Task Outsiders_cannot_see_or_add_project_documents()
    {
        var w = new World();
        var doc = await w.As(BrOwner, Roles.Employee).Documents.CreateTextAsync(GA, new CreateTextDocumentRequest("Notes", ProjectDocumentCategory.Meeting, "hello"));
        var outsider = w.As(Outsider, Roles.Employee);
        await Assert.ThrowsAsync<NotFoundException>(() => outsider.Documents.GetAllAsync(GA));
        await Assert.ThrowsAsync<NotFoundException>(() => outsider.Documents.GetTextAsync(GA, doc.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => outsider.Documents.CreateTextAsync(GA, new CreateTextDocumentRequest("x", ProjectDocumentCategory.Other, "x")));
        Assert.Empty(await outsider.Documents.SearchAsync("hello", null));
        // A document id from another project is not reachable through this one.
        await Assert.ThrowsAsync<NotFoundException>(() => w.AsAdmin().Documents.GetTextAsync(TV, doc.Id));
    }

    [Fact]
    public async Task Only_the_uploader_and_project_managers_delete_documents()
    {
        var w = new World();
        var doc = await w.As(Dev1, Roles.Employee).Documents.CreateTextAsync(GA, new CreateTextDocumentRequest("Dev notes", ProjectDocumentCategory.Technical, "x"));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As(FeLead, Roles.Employee).Documents.DeleteAsync(GA, doc.Id));
        await w.As(BrOwner, Roles.Employee).Documents.DeleteAsync(GA, doc.Id); // BR Owner manages the project
        Assert.Empty(await w.AsAdmin().Documents.GetAllAsync(GA));
        Assert.Empty(w.Files.Saved);
    }

    [Fact]
    public async Task Long_documents_are_read_in_pages()
    {
        var w = new World();
        var content = string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i:0000}\n"));
        var doc = await w.As(Dev1, Roles.Employee).Documents.CreateTextAsync(GA, new CreateTextDocumentRequest("Long", ProjectDocumentCategory.Technical, content));
        var reader = w.As(Dev1, Roles.Employee).Documents;

        var all = new StringBuilder();
        var offset = 0;
        DocumentTextDto page;
        do
        {
            page = await reader.GetTextAsync(GA, doc.Id, offset, 10_000);
            all.Append(page.Text);
            offset = page.Offset + page.Text.Length;
        } while (page.HasMore);
        Assert.Equal(content, all.ToString());
        Assert.Equal(content.Length, page.TotalLength);
    }

    [Fact]
    public async Task Documents_can_be_searched_with_snippets()
    {
        var w = new World();
        var bro = w.As(BrOwner, Roles.Employee);
        await bro.Documents.CreateTextAsync(GA, new CreateTextDocumentRequest("BR", ProjectDocumentCategory.BusinessRequirements,
            "Intro.\nThe driver pays with Fawry or a card.\nRefunds go back to the same card."));
        await w.AsAdmin().Documents.CreateTextAsync(TV, new CreateTextDocumentRequest("TV spec", ProjectDocumentCategory.Technical, "card layout"));

        var hits = await bro.Documents.SearchAsync("CARD", null);
        Assert.Equal(2, hits.Count); // two matches in GA's BR; TV is not visible to the BR Owner
        Assert.All(hits, h => Assert.Equal("GA", h.ProjectCode));
        Assert.Contains("Fawry or a card", hits[0].Snippet);
        Assert.Equal(3, (await w.AsAdmin().Documents.SearchAsync("card", null)).Count);
        await Assert.ThrowsAsync<BadRequestException>(() => bro.Documents.SearchAsync("c", null));
    }

    [Fact]
    public async Task Document_uploads_are_checked_like_task_files()
    {
        var w = new World();
        var dev = w.As(Dev1, Roles.Employee).Documents;
        await Assert.ThrowsAsync<BadRequestException>(() => dev.UploadAsync(GA, null, ProjectDocumentCategory.Other, "run.exe", null, 10, Text("MZ........")));
        await Assert.ThrowsAsync<BadRequestException>(() => dev.UploadAsync(GA, null, ProjectDocumentCategory.Other, "fake.png", "image/png", 20, Text("<html>not an image</html>")));
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0 };
        var img = await dev.UploadAsync(GA, null, ProjectDocumentCategory.Design, "home.png", "image/png", png.Length, new MemoryStream(png));
        Assert.Equal(AttachmentKind.Image, img.Kind);
        Assert.Equal("home", img.Title);
        Assert.False(img.HasText);
        await Assert.ThrowsAsync<BadRequestException>(() => dev.GetTextAsync(GA, img.Id));
    }

    // ------------------------------------------------------------------ text extraction

    private static readonly DocumentTextExtractor Extractor = new(NullLogger<DocumentTextExtractor>.Instance);

    [Fact]
    public void Word_documents_keep_headings_and_tables()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            W.Paragraph Para(string text, string? style = null) => new(
                style is null ? new W.ParagraphProperties() : new W.ParagraphProperties(new W.ParagraphStyleId { Val = style }),
                new W.Run(new W.Text(text)));
            main.Document = new W.Document(new W.Body(
                Para("Login", "Heading2"),
                Para("المستخدم يسجل الدخول برقم الموبايل"),
                new W.Table(new W.TableRow(
                    new W.TableCell(Para("US-AUTH-01")), new W.TableCell(Para("OTP login"))))));
        }
        ms.Position = 0;
        var text = Extractor.Extract(ms, ".docx")!;
        Assert.Contains("## Login", text);
        Assert.Contains("المستخدم يسجل الدخول برقم الموبايل", text);
        Assert.Contains("| US-AUTH-01 | OTP login |", text);
    }

    [Fact]
    public void Excel_sheets_are_read_row_by_row()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Stories");
            ws.Cell(1, 1).Value = "Id"; ws.Cell(1, 2).Value = "Story";
            ws.Cell(2, 1).Value = "US-01"; ws.Cell(2, 2).Value = "Book a car wash";
            wb.SaveAs(ms);
        }
        ms.Position = 0;
        var text = Extractor.Extract(ms, ".xlsx")!;
        Assert.Contains("[Sheet Stories]", text);
        Assert.Contains("US-01\tBook a car wash", text);
    }

    [Fact]
    public void Pdf_text_is_extracted_per_page()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var bytes = Document.Create(c =>
        {
            c.Page(p => p.Content().Text("Payment must support Fawry"));
            c.Page(p => p.Content().Text("Second page about refunds"));
        }).GeneratePdf();
        var text = Extractor.Extract(new MemoryStream(bytes), ".pdf")!;
        Assert.Contains("[Page 1]", text);
        Assert.Contains("Payment must support Fawry", text);
        Assert.Contains("[Page 2]", text);
        Assert.Contains("refunds", text);
    }

    [Fact]
    public void Damaged_files_and_unsupported_formats_give_no_text()
    {
        Assert.Null(Extractor.Extract(new MemoryStream(Encoding.ASCII.GetBytes("not really a pdf")), ".pdf"));
        Assert.Null(Extractor.Extract(new MemoryStream([1, 2, 3]), ".docx"));
        Assert.False(Extractor.Supports(".doc"));
        Assert.True(Extractor.Supports(".PDF"));
    }

    // ------------------------------------------------------------------ PR link + "via Claude"

    [Fact]
    public async Task Assignees_link_the_pull_request()
    {
        var w = new World();
        var t = await w.As(FeLead, Roles.Employee).Tasks.CreateAsync(NewTask("build login", GA, [Dev1]));
        var d = await w.As(Dev1, Roles.Employee).Tasks.SetPullRequestAsync(t.Task.Id, new SetPullRequestRequest("https://github.com/vetanoia/garas/pull/12"));
        Assert.Equal("https://github.com/vetanoia/garas/pull/12", d.PullRequestUrl);
        // Someone outside the project can't even see the task.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            w.As(Dev2, Roles.Employee).Tasks.SetPullRequestAsync(t.Task.Id, new SetPullRequestRequest("https://x.test/1")));
        var history = await w.As(Dev1, Roles.Employee).Tasks.GetHistoryAsync(t.Task.Id);
        Assert.Contains(history, h => h.Action == "PullRequestLinked");
        Assert.False(new SetPullRequestRequestValidator().Validate(new SetPullRequestRequest("javascript:alert(1)")).IsValid);
    }

    [Fact]
    public async Task Changes_made_through_Claude_are_marked()
    {
        var w = new World();
        var t = await w.As(FeLead, Roles.Employee).Tasks.CreateAsync(NewTask("build login", GA, [Dev1]));
        var claude = w.ViaClaude(Dev1, Roles.Employee);
        await claude.Tasks.ChangeStatusAsync(t.Task.Id, new UpdateTaskStatusRequest(TaskItemStatus.InProgress, null, null));
        var comment = await claude.Tasks.AddCommentAsync(t.Task.Id, new AddCommentRequest("Started 🚀"));
        var doc = await claude.Documents.CreateTextAsync(GA, new CreateTextDocumentRequest("Standup", ProjectDocumentCategory.Meeting, "notes"));

        Assert.Equal("Claude", comment.Source);
        Assert.Equal("Claude", doc.Source);
        var history = await w.As(Dev1, Roles.Employee).Tasks.GetHistoryAsync(t.Task.Id);
        Assert.Equal("Claude", history.First(h => h.Action == "StatusChanged").Source);
        Assert.Null(history.First(h => h.Action == "Created").Source); // made in the app
        var details = await w.As(Dev1, Roles.Employee).Tasks.GetAsync(t.Task.Id);
        Assert.Equal("Claude", details.Comments.Single().Source);
    }
}
