using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class QuickNoteServiceTests
{
    [Fact]
    public async Task AddQuickNoteAsync_ShouldCreateTheQuickNotesFolderOnce_AndNestEveryNoteUnderIt()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.Service.AddQuickNoteAsync("First idea", "Some body text", "owner");
        var second = await fixture.Service.AddQuickNoteAsync("Second idea", "More body text", "owner");

        var folders = await fixture.Db.WikiPages
            .Where(page => page.SystemKey == QuickNoteService.QuickNotesFolderSystemKey)
            .ToListAsync();
        folders.Should().ContainSingle();
        var folder = folders[0];

        first.ParentWikiPageId.Should().Be(folder.Id);
        second.ParentWikiPageId.Should().Be(folder.Id);
    }

    [Fact]
    public async Task AddQuickNoteAsync_ShouldStoreTheBodyAsAnEditableRichTextBlock()
    {
        await using var fixture = await Fixture.CreateAsync();

        var note = await fixture.Service.AddQuickNoteAsync("Idea", "Remember to follow up **soon**", "owner");

        var blocks = WikiBlockJson.ParseBlocks(note.BlocksJson);
        blocks.Should().ContainSingle();
        blocks[0].Type.Should().Be(WikiBlockTypes.Paragraph);
        // Populated RichText (not just an opaque Props string) is what makes the note render
        // and stay editable in the interactive wiki block editor - see wiki-block-editor.js's
        // createContentEditable, which builds the DOM purely from block.richText.
        blocks[0].PlainText.Should().Be("Remember to follow up soon");
        blocks[0].RichText.Should().Contain(span => span.Text == "soon" && span.Bold);
    }

    [Fact]
    public async Task AddQuickNoteAsync_ShouldParseChecklistsListsAndTablesIntoNativeBlocks()
    {
        await using var fixture = await Fixture.CreateAsync();

        const string body = """
            - [ ] Call the vendor
            - [x] Draft the agenda

            - First point
            - Second point

            | Item | Qty |
            | --- | --- |
            | Widget | 3 |
            """;
        var note = await fixture.Service.AddQuickNoteAsync("Idea", body, "owner");

        var blocks = WikiBlockJson.ParseBlocks(note.BlocksJson);
        blocks.Where(b => b.Type == WikiBlockTypes.ToDo).Should().HaveCount(2);
        blocks.Should().Contain(b => b.Type == WikiBlockTypes.ToDo && b.Props["checked"] == "false" && b.PlainText == "Call the vendor");
        blocks.Should().Contain(b => b.Type == WikiBlockTypes.ToDo && b.Props["checked"] == "true" && b.PlainText == "Draft the agenda");
        blocks.Where(b => b.Type == WikiBlockTypes.BulletedListItem).Should().HaveCount(2);
        blocks.Should().Contain(b => b.Type == WikiBlockTypes.Table);
    }

    [Fact]
    public async Task AddQuickNoteAsync_ShouldRebuildTheFolderIndexWithAClickableWikilinkPerNote()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.Service.AddQuickNoteAsync("First idea", "Body one", "owner");
        var second = await fixture.Service.AddQuickNoteAsync("Second idea", "Body two", "owner");

        var folder = await fixture.Db.WikiPages
            .SingleAsync(page => page.SystemKey == QuickNoteService.QuickNotesFolderSystemKey);
        var indexBlocks = WikiBlockJson.ParseBlocks(folder.BlocksJson);

        indexBlocks.Should().HaveCount(2);
        indexBlocks.Should().OnlyContain(block => block.Type == WikiBlockTypes.BulletedListItem);
        var links = indexBlocks.Select(block => block.RichText.Single().Link).ToList();
        links.Should().Contain($"wikilink:{first.Id}");
        links.Should().Contain($"wikilink:{second.Id}");
        var titles = indexBlocks.Select(block => block.RichText.Single().Text).ToList();
        titles.Should().Contain("First idea");
        titles.Should().Contain("Second idea");
    }

    [Fact]
    public async Task AddQuickNoteAsync_ShouldDefaultTheTitle_WhenBlank()
    {
        await using var fixture = await Fixture.CreateAsync();

        var note = await fixture.Service.AddQuickNoteAsync("   ", "Body only", "owner");

        note.Title.Should().Be("Untitled note");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            Service = new QuickNoteService(db, new WikiService(db));
        }

        public ApplicationDbContext Db { get; }
        public QuickNoteService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
