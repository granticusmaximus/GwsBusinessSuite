using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using UglyToad.PdfPig;

namespace GwsBusinessSuite.Tests;

// Sentinel item 20: export to PDF and Word.
public sealed class SentinelPageExportTests
{
    private static WikiBlock Block(string type, string text, Dictionary<string, string>? props = null, int indent = 0, IReadOnlyList<WikiRichTextSpan>? spans = null) =>
        new(Guid.NewGuid(), type, indent, spans ?? (text.Length == 0 ? [] : [new WikiRichTextSpan(text)]), props ?? new Dictionary<string, string>());

    private static byte[] Image(SKEncodedImageFormat format, int width = 40, int height = 20)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.SteelBlue);
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    [Fact]
    public void Flatten_ShouldNumberListsPerLevel_MarkToDos_AndReadBothTableForms()
    {
        var elements = SentinelExportFlattener.Flatten([
            Block(WikiBlockTypes.NumberedListItem, "One"),
            Block(WikiBlockTypes.NumberedListItem, "One a", indent: 1),
            Block(WikiBlockTypes.NumberedListItem, "Two"),
            Block(WikiBlockTypes.Paragraph, "Break"),
            Block(WikiBlockTypes.NumberedListItem, "Again one"),
            Block(WikiBlockTypes.ToDo, "Done", new() { ["checked"] = "true" }),
            Block(WikiBlockTypes.Table, "Name | Role\n--- | ---\nGrant | Owner"),
            Block(WikiBlockTypes.Table, "", new() { ["tableJson"] = """[[[{"text":"A"}],[{"text":"B","bold":true}]],[[{"text":"1"}],[{"text":"2"}]]]""", ["hasColumnHeader"] = "false" }),
            Block(WikiBlockTypes.Markdown, "", new() { ["content"] = "## Old heading\n- old item" }),
            Block(WikiBlockTypes.Video, "Demo", new() { ["url"] = "https://videos.example/demo.mp4" }),
            Block(WikiBlockTypes.Columns, "Left text ||| Right text")
        ]);

        elements.OfType<ExportListItem>().Select(i => (i.Marker, SentinelExportFlattener.Text(i.Text), i.Level)).Should().Equal(
            ("1.", "One", 0), ("1.", "One a", 1), ("2.", "Two", 0), ("1.", "Again one", 0), ("☑", "Done", 0), ("•", "old item", 0));
        var tables = elements.OfType<ExportTable>().ToList();
        tables[0].Rows.Select(r => string.Join("|", r.Select(SentinelExportFlattener.Text))).Should().Equal("Name|Role", "Grant|Owner");
        tables[1].HasHeader.Should().BeFalse();
        tables[1].Rows[0][1][0].Bold.Should().BeTrue();
        elements.OfType<ExportHeading>().Should().ContainSingle(h => h.Level == 2 && SentinelExportFlattener.Text(h.Text) == "Old heading");
        elements.OfType<ExportNote>().Should().ContainSingle().Which.Should().Be(new ExportNote("Video: Demo", "https://videos.example/demo.mp4"));
        elements.OfType<ExportParagraph>().Select(p => SentinelExportFlattener.Text(p.Text)).Should().Contain(["Left text", "Right text"]);
        SentinelExportFlattener.ExternalLink("wikilink:abc").Should().BeNull();
        SentinelExportFlattener.ExternalLink("javascript:alert(1)").Should().BeNull();
        SentinelExportFlattener.ExternalLink("https://gwsapp.net").Should().Be("https://gwsapp.net");
    }

    [Fact]
    public void Normalize_ShouldKeepPngAndJpeg_ConvertOtherFormats_AndRejectNonImages()
    {
        SentinelPageExportService.Normalize(Image(SKEncodedImageFormat.Png))!.Format.Should().Be("png");
        SentinelPageExportService.Normalize(Image(SKEncodedImageFormat.Jpeg))!.Format.Should().Be("jpeg");
        var webp = SentinelPageExportService.Normalize(Image(SKEncodedImageFormat.Webp, 30, 10))!;
        webp.Format.Should().Be("png");
        (webp.Width, webp.Height).Should().Be((30, 10));
        SentinelPageExportService.Normalize("not an image"u8.ToArray()).Should().BeNull();
    }

    [Fact]
    public async Task Export_ShouldProducePdfAndWordFiles_WithTextImagesTables_AndRespectAccessAndTrash()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        var picture = new SentinelImportedFile { NotionBlockId = "test", FileName = "chart.webp", ContentType = "image/webp", Content = Image(SKEncodedImageFormat.Webp, 120, 60), SizeBytes = 1, CreatedBy = "grant" };
        db.SentinelImportedFiles.Add(picture);
        var root = new WikiPage { Title = "Launch plan", Slug = "launch-plan" };
        db.WikiPages.Add(root);
        await db.SaveChangesAsync();
        root.BlocksJson = WikiBlockJson.Serialize([
            Block(WikiBlockTypes.Heading1, "Goals"),
            Block(WikiBlockTypes.Paragraph, "", spans: [new WikiRichTextSpan("Read "), new WikiRichTextSpan("the brief", Bold: true, Link: "https://gwsapp.net/brief"), new WikiRichTextSpan(".")]),
            Block(WikiBlockTypes.ToDo, "Book venue", new() { ["checked"] = "true" }),
            Block(WikiBlockTypes.Code, "var x = 1;\nvar y = 2;", new() { ["language"] = "csharp" }),
            Block(WikiBlockTypes.Image, "Sales chart", new() { ["url"] = $"/admin/sentinel/files/{picture.Id}" }),
            Block(WikiBlockTypes.Image, "Outside picture", new() { ["url"] = "https://elsewhere.example/a.png" }),
            Block(WikiBlockTypes.Table, "Name | Owner\nVenue | Sam")
        ]);
        var visibleChild = new WikiPage { Title = "Budget", Slug = "budget", ParentWikiPageId = root.Id, SortOrder = 1, BlocksJson = WikiBlockJson.Serialize([Block(WikiBlockTypes.Paragraph, "Budget text")]) };
        var trashedChild = new WikiPage { Title = "Salaries", Slug = "salaries", ParentWikiPageId = root.Id, SortOrder = 2, TrashedAt = DateTimeOffset.UtcNow,
            BlocksJson = WikiBlockJson.Serialize([Block(WikiBlockTypes.Paragraph, "Private text")]) };
        // A grant on a page covers everything under it, so sam can export this tree; "Other" is
        // granted only to grant, so sam can't export it at all.
        var other = new WikiPage { Title = "Other", Slug = "other" };
        db.WikiPages.AddRange(visibleChild, trashedChild, other);
        await db.SaveChangesAsync();
        db.SentinelResourcePermissions.AddRange(
            new SentinelResourcePermission { TargetId = root.Id, IsDatabase = false, Username = "sam", AccessLevel = SentinelAccessLevels.View },
            new SentinelResourcePermission { TargetId = other.Id, IsDatabase = false, Username = "grant", AccessLevel = SentinelAccessLevels.FullAccess });
        await db.SaveChangesAsync();
        var service = new SentinelPageExportService(db, TimeProvider.System, NullLogger<SentinelPageExportService>.Instance, accessService: new SentinelAccessService(db));

        var built = await service.BuildAsync(root.Id, "sam", includeSubPages: true);
        built!.Pages.Select(p => (p.Title, p.Depth)).Should().Equal(("Launch plan", 0), ("Budget", 1));
        built.Images.Should().ContainSingle().Which.Value.Format.Should().Be("png", "the WebP is converted so Word can open it");
        (await service.BuildAsync(root.Id, "sam", includeSubPages: false))!.Pages.Should().ContainSingle();
        (await service.BuildAsync(other.Id, "sam", includeSubPages: false)).Should().BeNull("sam has no access to it");
        (await service.BuildAsync(other.Id, "grant", includeSubPages: false)).Should().NotBeNull();
        (await service.BuildAsync(trashedChild.Id, "grant", includeSubPages: false)).Should().BeNull("it's in the trash");

        var pdf = await service.ExportAsync(root.Id, "sam", SentinelExportFormat.Pdf, includeSubPages: true);
        pdf!.FileName.Should().Be("launch-plan.pdf");
        pdf.ContentType.Should().Be("application/pdf");
        using (var document = PdfDocument.Open(pdf.Content))
        {
            var text = string.Join(" ", document.GetPages().Select(p => p.Text));
            text.Should().Contain("Launch plan").And.Contain("Goals").And.Contain("the brief").And.Contain("[x]Book venue")
                .And.Contain("var x = 1;").And.Contain("Image: Outside picture").And.Contain("Venue").And.Contain("Budget text")
                .And.NotContain("Private text");
            document.GetPages().Sum(p => p.GetImages().Count()).Should().BeGreaterThanOrEqualTo(1);
            document.NumberOfPages.Should().BeGreaterThanOrEqualTo(2, "each sub-page starts on a new page");
        }

        var docx = await service.ExportAsync(root.Id, "sam", SentinelExportFormat.Docx, includeSubPages: true);
        docx!.FileName.Should().Be("launch-plan.docx");
        using (var word = WordprocessingDocument.Open(new MemoryStream(docx.Content), false))
        {
            new OpenXmlValidator().Validate(word).Select(e => $"{e.Path?.XPath}: {e.Description}").Should().BeEmpty("Word must open it cleanly");
            var body = word.MainDocumentPart!.Document!.Body!.InnerText;
            body.Should().Contain("Launch plan").And.Contain("the brief").And.Contain("Book venue").And.Contain("Venue").And.Contain("Budget text").And.NotContain("Private text");
            word.MainDocumentPart.ImageParts.Should().ContainSingle();
            word.MainDocumentPart.HyperlinkRelationships.Select(r => r.Uri.ToString()).Should().Contain(["https://gwsapp.net/brief", "https://elsewhere.example/a.png"]);
            word.MainDocumentPart.Document.Body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>().Should().ContainSingle();
            word.MainDocumentPart.Document.Body.Descendants<DocumentFormat.OpenXml.Wordprocessing.ParagraphStyleId>().Select(s => s.Val!.Value)
                .Should().Contain(["Title", "Heading1"]);
        }
    }
}
