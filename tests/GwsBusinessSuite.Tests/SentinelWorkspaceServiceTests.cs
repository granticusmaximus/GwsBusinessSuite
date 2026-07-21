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
        var sentinel = new SentinelWorkspaceService(db);

        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Operations",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("The launch sequence uses the blue switch.")],
                    new Dictionary<string, string>())])
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

        pageResults.Should().ContainSingle(result => !result.IsDatabase && result.Title == "Operations" && result.MatchKind == "Page content");
        databaseResults.Should().ContainSingle(result => result.IsDatabase && result.Title == "Projects" && result.MatchKind == "Database content");
        databasePageResults.Should().ContainSingle(result => result.IsDatabase && result.Title == "Projects" && result.MatchKind == "Database content");
    }

    [Fact]
    public async Task GetBacklinksAsync_ShouldFindStructuredAndLegacyLinks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new WikiService(db);
        var sentinel = new SentinelWorkspaceService(db);

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

    private static async Task<ApplicationDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
