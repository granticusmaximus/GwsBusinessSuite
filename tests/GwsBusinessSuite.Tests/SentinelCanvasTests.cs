using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelCanvasTests
{
    private static readonly Guid LinkedPage = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    private static string Doc(params object[] items) => JsonSerializer.Serialize(new { v = 1, items, view = new { x = 10, y = 20, zoom = 1.5 } });

    [Fact]
    public void NormalizeDoc_ShouldKeepKnownFieldsOnly_AndDropBadItems()
    {
        var json = Doc(
            new { id = "a1", type = "sticky", x = 10, y = 20, w = 200, h = 160, text = "Plan", color = "#fff3a3", onclick = "alert(1)" },
            new { id = "bad id!", type = "sticky", x = 0, y = 0 },
            new { id = "b2", type = "script", x = 0, y = 0 },
            new { id = "c3", type = "card", x = 0, y = 0, pageId = "not-a-guid" },
            new { id = "d4", type = "rect", x = 1e12, y = 0, color = "red" },
            new { id = "a1", type = "text", x = 0, y = 0 });

        var doc = JsonDocument.Parse(SentinelCanvas.NormalizeDoc(json)).RootElement;
        var items = doc.GetProperty("items").EnumerateArray().ToList();

        items.Select(item => item.GetProperty("id").GetString()).Should().Equal("a1", "d4");
        items[0].TryGetProperty("onclick", out _).Should().BeFalse();
        items[0].GetProperty("color").GetString().Should().Be("#fff3a3");
        items[1].GetProperty("x").GetDouble().Should().Be(1_000_000);
        items[1].TryGetProperty("color", out _).Should().BeFalse();
        doc.GetProperty("view").GetProperty("zoom").GetDouble().Should().Be(1.5);
    }

    [Fact]
    public void NormalizeDoc_ShouldDetachArrowsFromMissingItems_AndRefuseOversizedOrCorruptBoards()
    {
        var json = Doc(
            new { id = "a", type = "rect", x = 0, y = 0, w = 10, h = 10 },
            new { id = "c", type = "connector", x = 0, y = 0, x2 = 50, y2 = 50, from = "a", to = "gone" });

        var connector = JsonDocument.Parse(SentinelCanvas.NormalizeDoc(json)).RootElement.GetProperty("items")[1];
        connector.GetProperty("from").GetString().Should().Be("a");
        connector.TryGetProperty("to", out _).Should().BeFalse();

        FluentActions.Invoking(() => SentinelCanvas.NormalizeDoc("{not json")).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => SentinelCanvas.NormalizeDoc(new string(' ', SentinelCanvas.MaxDocLength + 1) + "{}"))
            .Should().Throw<InvalidOperationException>();
        var tooMany = Doc(Enumerable.Range(0, SentinelCanvas.MaxItems + 1).Select(i => (object)new { id = $"i{i}", type = "text", x = 0, y = 0 }).ToArray());
        FluentActions.Invoking(() => SentinelCanvas.NormalizeDoc(tooMany)).Should().Throw<InvalidOperationException>();
        SentinelCanvas.NormalizeDoc("").Should().Be(SentinelCanvas.EmptyDoc);
    }

    [Fact]
    public void RichText_ShouldReadFramesFirst_ThenTopToBottom_WithPageCardsAsLinks()
    {
        var doc = SentinelCanvas.NormalizeDoc(Doc(
            new { id = "s2", type = "sticky", x = 0, y = 300, text = "Later" },
            new { id = "s1", type = "sticky", x = 0, y = 100, text = " First " },
            new { id = "f", type = "frame", x = 0, y = 900, text = "Sprint" },
            new { id = "k", type = "card", x = 0, y = 200, pageId = LinkedPage.ToString(), title = "Roadmap" },
            new { id = "e", type = "rect", x = 0, y = 50, text = "  " }));

        var spans = SentinelCanvas.RichText(doc);

        string.Concat(spans.Select(span => span.Text)).Should().Be("Sprint\nFirst\nRoadmap\nLater");
        spans.Should().ContainSingle(span => span.Link == $"wikilink:{LinkedPage}");
        SentinelCanvas.PageReferences(doc).Should().Equal(LinkedPage);
    }

    [Fact]
    public void HtmlRenderer_ShouldDrawReadOnly_WithoutCardTitles()
    {
        var block = SentinelCanvas.Normalize(new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Canvas, 0, [],
            new Dictionary<string, string> { ["doc"] = Doc(
                new { id = "s", type = "sticky", x = 0, y = 0, text = "<b>hi</b>" },
                new { id = "k", type = "card", x = 0, y = 200, pageId = LinkedPage.ToString(), title = "Secret plan" }) }));

        var html = WikiBlockHtmlRenderer.RenderPage([block]);

        html.Should().Contain("wiki-canvas-static").And.Contain("&lt;b&gt;hi&lt;/b&gt;").And.NotContain("<b>hi");
        html.Should().NotContain("Secret plan");
    }

    [Fact]
    public async Task SavePage_ShouldNormalizeTheBoard_AndItsCardsShouldCountAsBacklinks()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.Add(new AppUser { Username = "grant", Role = AppRoles.Admin });
        await db.SaveChangesAsync();
        var wiki = new WikiService(db);
        var target = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Roadmap" }, "grant");
        var board = new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Canvas, 0, [new WikiRichTextSpan("client-sent text is ignored")],
            new Dictionary<string, string> { ["doc"] = Doc(
                new { id = "s", type = "sticky", x = 0, y = 0, text = "Kickoff", junk = 1 },
                new { id = "k", type = "card", x = 0, y = 200, pageId = target.Id.ToString(), title = "Roadmap" }) });

        var saved = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Board", BlocksJson = WikiBlockJson.Serialize([board]) }, "grant");

        var stored = WikiBlockJson.ParseBlocks(saved.BlocksJson).Single();
        SentinelCanvas.IsCanvasPage([stored]).Should().BeTrue();
        stored.PlainText.Should().Be("Kickoff\nRoadmap");
        stored.Props["doc"].Should().NotContain("junk");
        var backlinks = await new SentinelWorkspaceService(db, TimeProvider.System, new SentinelAccessService(db)).GetBacklinksAsync(target.Id, "grant");
        backlinks.Should().Contain(link => link.SourcePageTitle == "Board");

        var corrupt = board with { Props = new Dictionary<string, string> { ["doc"] = "{broken" } };
        await FluentActions.Invoking(() => wiki.SavePageAsync(new WikiPageEditorModel { Title = "Bad", BlocksJson = WikiBlockJson.Serialize([corrupt]) }, "grant"))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Exports_ShouldListTheBoardsWords()
    {
        var block = SentinelCanvas.Normalize(new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Canvas, 0, [],
            new Dictionary<string, string> { ["doc"] = Doc(new { id = "s", type = "sticky", x = 0, y = 0, text = "Kickoff" }, new { id = "t", type = "text", x = 0, y = 50, text = "Ship it" }) }));

        WikiMarkdownExporter.ExportPage("Board", [block]).Should().Contain("**Whiteboard**").And.Contain("- Kickoff\n- Ship it");
        SentinelExportFlattener.Flatten([block]).OfType<ExportListItem>().Select(item => string.Concat(item.Text.Select(span => span.Text)))
            .Should().Equal("Kickoff", "Ship it");
    }
}
