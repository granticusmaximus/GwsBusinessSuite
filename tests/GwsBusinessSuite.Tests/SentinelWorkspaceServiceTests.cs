using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelWorkspaceServiceTests
{
    [Fact]
    public async Task SearchAsync_ShouldFindPageBlockContentAndDatabaseRowValues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var databases = new WikiDatabaseService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);

        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Operations",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("The launch sequence uses the blue switch.")],
                    new Dictionary<string, string>()),
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Embed, 0, [],
                    new Dictionary<string, string>
                    {
                        ["url"] = "/admin/sentinel/files/file-id",
                        ["fileName"] = "Quarterly plan.pdf"
                    })])
        }, "u");

        var database = await databases.CreateDatabaseAsync("Projects", null, "u");
        var titleProperty = database.Properties.Single(property => property.Type == GwsBusinessSuite.Domain.Entities.WikiDatabasePropertyTypes.Title);
        var values = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(values, titleProperty.Id, "Northstar migration");
        await databases.SaveRowAsync(database.Id, new WikiDatabaseRowEditor
        {
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Decision log for the launch window")],
                    new Dictionary<string, string>())]),
            Values = values.ToDictionary(pair => pair.Key, pair => pair.Value)
        }, "u");

        var pageResults = await sentinel.SearchAsync("blue switch");
        var databaseResults = await sentinel.SearchAsync("Northstar");
        var databasePageResults = await sentinel.SearchAsync("Decision log");
        var attachmentResults = await sentinel.SearchAsync("Quarterly plan");

        pageResults.Should().ContainSingle(result => !result.IsDatabase && result.Title == "Operations" && result.MatchKind == "Page content");
        databaseResults.Should().ContainSingle(result => result.IsDatabase && result.Title == "Projects" && result.MatchKind == "Database content");
        databasePageResults.Should().ContainSingle(result => result.IsDatabase && result.Title == "Projects" && result.MatchKind == "Database content");
        attachmentResults.Should().ContainSingle(result => !result.IsDatabase && result.Title == "Operations" && result.MatchKind == "Page content");
    }

    [Fact]
    public async Task GetBacklinksAsync_ShouldFindStructuredAndLegacyLinks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);

        var target = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Runbook" }, "u");
        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Structured source",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Open the runbook", Link: $"wikilink:{target.Id}")],
                    new Dictionary<string, string>())])
        }, "u");
        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Legacy source",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Markdown, 0, [],
                    new Dictionary<string, string> { ["content"] = "See [[Runbook]] before deploying." })])
        }, "u");

        var backlinks = await sentinel.GetBacklinksAsync(target.Id);

        backlinks.Select(link => link.SourcePageTitle).Should().BeEquivalentTo("Structured source", "Legacy source");
    }

    [Fact]
    public async Task SearchAsync_ShouldRequireEveryTokenAndReturnMatchedTerms()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var sentinel = new SentinelWorkspaceService(db, TimeProvider.System);

        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Deployment Runbook",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Blue production checklist")], new Dictionary<string, string>())])
        }, "u");
        await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Deployment Notes" }, "u");

        var results = await sentinel.SearchAsync("deployment blue");

        results.Should().ContainSingle(result => result.Title == "Deployment Runbook");
        results[0].MatchedTerms.Should().Equal("deployment", "blue");
    }

    [Fact]
    public async Task NavigationAsync_ShouldTrackPerUserFavoritesAndRecents()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var wiki = new WikiService(db);
        var databases = new WikiDatabaseService(db);
        var sentinel = new SentinelWorkspaceService(db, time);
        var page = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Runbook" }, "u");
        var database = await databases.CreateDatabaseAsync("Projects", null, "u");

        await sentinel.RecordOpenedAsync("Grant", page.Id, false);
        time.Advance(TimeSpan.FromMinutes(1));
        await sentinel.RecordOpenedAsync("Grant", database.Id, true);
        (await sentinel.ToggleFavoriteAsync("Grant", page.Id, false)).Should().BeTrue();

        var state = await sentinel.GetNavigationAsync("GRANT");

        state.Favorites.Should().ContainSingle(item => item.Id == page.Id && !item.IsDatabase);
        state.Recents.Select(item => item.Id).Should().Equal(database.Id, page.Id);
        (await sentinel.GetNavigationAsync("another-user")).Recents.Should().BeEmpty();
    }

    [Fact]
    public async Task MentionsAsync_ShouldSuggestPeopleAndDatesAndFindStructuredUserMentions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        db.AppUsers.Add(new GwsBusinessSuite.Domain.Entities.AppUser { Username = "Grant", IsActive = true });
        await db.SaveChangesAsync();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var wiki = new WikiService(db);
        var sentinel = new SentinelWorkspaceService(db, time);
        var source = await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Launch plan",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("@Grant", Link: "usermention:grant")], new Dictionary<string, string>())])
        }, "u");

        var people = await sentinel.SearchMentionSuggestionsAsync("gra");
        var dates = await sentinel.SearchMentionSuggestionsAsync("tom");
        var mentions = await sentinel.GetMentionsAsync("GRANT");

        people.Should().ContainSingle(item => item.Kind == "user" && item.Value == "Grant");
        dates.Should().ContainSingle(item => item.Kind == "date" && item.Value == "2026-07-21");
        mentions.Should().ContainSingle(item => item.SourcePageId == source.Id);
    }

    [Fact]
    public void RenderRichText_ShouldRenderMentionsAsNonNavigatingStyledLinks()
    {
        var html = WikiBlockHtmlRenderer.RenderRichText([
            new WikiRichTextSpan("@Grant", Link: "usermention:grant"),
            new WikiRichTextSpan(" "),
            new WikiRichTextSpan("@tomorrow", Link: "datemention:2026-07-21")
        ]);

        html.Should().Contain("class=\"wiki-mention\"");
        html.Should().Contain("href=\"usermention:grant\"");
        html.Should().Contain("href=\"datemention:2026-07-21\"");
        html.Should().NotContain("target=\"_blank\"");
    }

    private static async Task<ApplicationDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan by) => _utcNow += by;
    }
}
