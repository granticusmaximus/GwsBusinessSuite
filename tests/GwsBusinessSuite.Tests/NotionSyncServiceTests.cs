using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NotionSyncServiceTests
{
    [Fact]
    public async Task SyncAsync_ShouldCreateThenUpdateByNotionIdWithoutDuplicating()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("page-1", "First")];

        (await fixture.Service.SyncAsync()).IsSuccess.Should().BeTrue();
        fixture.Notion.SearchResults = [Page("page-1", "Renamed")];
        (await fixture.Service.SyncAsync()).IsSuccess.Should().BeTrue();

        var pages = await fixture.Db.WikiPages.Where(p => p.NotionId == "page-1").ToListAsync();
        pages.Should().ContainSingle();
        pages[0].Title.Should().Be("Renamed");
        pages[0].NotionArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task SyncAsync_ShouldWirePageHierarchyFromParentDescriptor()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults =
        [
            Page("parent", "Parent"),
            Page("child", "Child", "{\"type\":\"page_id\",\"page_id\":\"parent\"}")
        ];

        await fixture.Service.SyncAsync();

        var parent = await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "parent");
        var child = await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "child");
        child.ParentWikiPageId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task SyncAsync_ShouldSoftArchiveMissingAndUpstreamArchivedPagesAndRestoreReturningPages()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("missing", "Missing"), Page("flagged", "Flagged")];
        await fixture.Service.SyncAsync();

        fixture.Notion.SearchResults = [Page("flagged", "Flagged", archived: true)];
        var archivedResult = await fixture.Service.SyncAsync();

        (await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "missing")).NotionArchivedAt.Should().NotBeNull();
        (await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "flagged")).NotionArchivedAt.Should().NotBeNull();
        archivedResult.Archived.Should().Be(2, "one missing page and one explicitly archived page changed state");

        fixture.Notion.SearchResults = [Page("missing", "Returned"), Page("flagged", "Flagged")];
        await fixture.Service.SyncAsync();
        (await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "missing")).NotionArchivedAt.Should().BeNull();
        (await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "flagged")).NotionArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task SyncAsync_ShouldExcludeDatabaseRowsFromTheWikiPageTree()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults =
        [
            Page("standalone", "Standalone"),
            Page("row-1", "Database row", "{\"type\":\"database_id\",\"database_id\":\"database-1\"}")
        ];

        await fixture.Service.SyncAsync();

        (await fixture.Db.WikiPages.Select(p => p.NotionId).ToListAsync()).Should().Equal("standalone");
    }

    [Fact]
    public async Task SyncAsync_ShouldImportDatabaseRowPageBlocks()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Database("database-1", "Projects")];
        fixture.Notion.DatabaseSchemas["database-1"] = Json("""
            {"properties":{"Name":{"id":"title","type":"title","title":{}}}}
            """);
        fixture.Notion.DatabaseRows["database-1"] =
        [
            Json("""
                {"object":"page","id":"row-1","parent":{"type":"database_id","database_id":"database-1"},"properties":{"Name":{"id":"title","type":"title","title":[{"plain_text":"First project"}]}}}
                """)
        ];
        fixture.Notion.BlockChildren["row-1"] =
        [
            Json("""
                {"object":"block","id":"block-1","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Project page notes"}]}}
                """)
        ];

        await fixture.Service.SyncAsync();

        var row = await fixture.Db.WikiDatabaseRows.SingleAsync(item => item.NotionId == "row-1");
        WikiBlockJson.ParseBlocks(row.BlocksJson).Should().ContainSingle(block => block.PlainText == "Project page notes");
    }

    private static JsonElement Page(string id, string title, string parent = "{\"type\":\"workspace\",\"workspace\":true}", bool archived = false) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["object"] = "page",
            ["id"] = id,
            ["archived"] = archived,
            ["parent"] = JsonDocument.Parse(parent).RootElement.Clone(),
            ["properties"] = new Dictionary<string, object?>
            {
                ["Name"] = new Dictionary<string, object?>
                {
                    ["type"] = "title",
                    ["title"] = new[] { new Dictionary<string, object?> { ["plain_text"] = title } }
                }
            }
        });

    private static JsonElement Database(string id, string title) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["object"] = "database",
            ["id"] = id,
            ["archived"] = false,
            ["parent"] = new Dictionary<string, object?> { ["type"] = "workspace", ["workspace"] = true },
            ["title"] = new[] { new Dictionary<string, object?> { ["plain_text"] = title } }
        });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class SyncFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SyncFixture(SqliteConnection connection, ApplicationDbContext db, FakeNotionService notion)
        {
            _connection = connection;
            Db = db;
            Notion = notion;
            Service = new NotionSyncService(db, notion, new FakeSecretProtector(), NullLogger<NotionSyncService>.Instance);
        }

        public ApplicationDbContext Db { get; }
        public FakeNotionService Notion { get; }
        public NotionSyncService Service { get; }

        public static async Task<SyncFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var notion = new FakeNotionService();
            var fixture = new SyncFixture(connection, db, notion);
            await fixture.Service.SaveSettingsAsync(new NotionConnectorSettingsView { IntegrationToken = "secret" });
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeNotionService : INotionService
    {
        public IReadOnlyList<JsonElement> SearchResults { get; set; } = [];
        public Dictionary<string, IReadOnlyList<JsonElement>> BlockChildren { get; } = new();
        public Dictionary<string, JsonElement> DatabaseSchemas { get; } = new();
        public Dictionary<string, IReadOnlyList<JsonElement>> DatabaseRows { get; } = new();

        public Task<NotionValidationResult> ValidateConnectionAsync(string integrationToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NotionValidationResult(true, "Connected.", "Test Workspace"));

        public Task<NotionPage> SearchAsync(string integrationToken, string? cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NotionPage(SearchResults, false, null));

        public Task<NotionPage> GetBlockChildrenAsync(string integrationToken, string blockId, string? cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NotionPage(BlockChildren.GetValueOrDefault(blockId) ?? [], false, null));

        public Task<JsonElement?> GetDatabaseAsync(string integrationToken, string databaseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(DatabaseSchemas.TryGetValue(databaseId, out var schema) ? (JsonElement?)schema : null);

        public Task<NotionPage> QueryDatabaseAsync(string integrationToken, string databaseId, string? cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NotionPage(DatabaseRows.GetValueOrDefault(databaseId) ?? [], false, null));
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"enc::{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue["enc::".Length..];
    }
}
