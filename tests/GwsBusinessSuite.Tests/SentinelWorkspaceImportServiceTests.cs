using System.IO.Compression;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelWorkspaceImportServiceTests
{
    [Fact]
    public async Task ImportAsync_ShouldRestoreEditableHierarchyDatabaseRowsAndAttachments()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string rootId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string childId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string databaseId = "cccccccccccccccccccccccccccccccc";
        const string rowId = "dddddddddddddddddddddddddddddddd";
        var archive = CreateArchive(
            ($"Project Hub {rootId}.md",
                "# Project Hub\n\nWelcome to the team.\n\n![Cover](Project%20Hub%20aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cover.png)"),
            ($"Project Hub {rootId}/Runbook {childId}.md",
                "## Deployment\n\n- Verify health\n- Notify the team"),
            ($"Project Hub {rootId}/Projects {databaseId}.csv",
                "Name,Owner\nLaunch,Grant\nDocumentation,Avery"),
            ($"Project Hub {rootId}/Projects {databaseId}/Launch {rowId}.md",
                "# Launch\n\nShip checklist"),
            ($"Project Hub {rootId}/cover.png", new byte[] { 1, 2, 3, 4 }));

        var result = await fixture.Service.ImportAsync(archive, "Owner");

        result.PagesCreated.Should().Be(2);
        result.DatabasesCreated.Should().Be(1);
        result.DatabaseRowsImported.Should().Be(2);
        result.FilesImported.Should().Be(1);

        var pages = await fixture.Db.WikiPages.OrderBy(page => page.Title).ToListAsync();
        var root = pages.Single(page => page.Title == "Project Hub");
        var child = pages.Single(page => page.Title == "Runbook");
        child.ParentWikiPageId.Should().Be(root.Id);
        WikiBlockJson.ParseBlocks(child.BlocksJson).Should().Contain(block =>
            block.Type == WikiBlockTypes.Heading2 && block.PlainText == "Deployment");

        var image = WikiBlockJson.ParseBlocks(root.BlocksJson)
            .Single(block => block.Type == WikiBlockTypes.Image);
        var importedFile = await fixture.Db.SentinelImportedFiles.SingleAsync();
        image.Props["url"].Should().Be($"/admin/sentinel/files/{importedFile.Id}");
        importedFile.Content.Should().Equal(1, 2, 3, 4);

        var database = await fixture.Db.WikiDatabases
            .Include(item => item.Properties)
            .Include(item => item.Rows)
            .SingleAsync();
        database.ParentWikiPageId.Should().Be(root.Id);
        database.NotionExportId.Should().Be(databaseId);
        database.Rows.Should().HaveCount(2);
        var titleProperty = database.Properties.Single(property =>
            property.Type == WikiDatabasePropertyTypes.Title);
        var launch = database.Rows.Single(row =>
            WikiPropertyValues.GetText(
                WikiPropertyValues.ParseObject(row.PropertyValuesJson),
                titleProperty.Id) == "Launch");
        launch.NotionExportId.Should().Be(rowId);
        WikiBlockJson.ParseBlocks(launch.BlocksJson).Should().Contain(block =>
            block.PlainText == "Ship checklist");
    }

    [Fact]
    public async Task ImportAsync_ShouldUpdateMatchingDocumentsWithoutDuplicatesAndCreateRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string pagePath = "Handbook eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee.md";
        const string databasePath = "Contacts ffffffffffffffffffffffffffffffff.csv";

        await fixture.Service.ImportAsync(CreateArchive(
            (pagePath, "# Handbook\n\nFirst edition"),
            (databasePath, "Name,Email\nGrant,old@example.test")), "Owner");
        var second = await fixture.Service.ImportAsync(CreateArchive(
            (pagePath, "# Handbook\n\nSecond edition"),
            (databasePath, "Name,Email\nGrant,new@example.test")), "Owner");

        second.PagesCreated.Should().Be(0);
        second.PagesUpdated.Should().Be(1);
        second.DatabasesCreated.Should().Be(0);
        second.DatabasesUpdated.Should().Be(1);
        (await fixture.Db.WikiPages.CountAsync()).Should().Be(1);
        (await fixture.Db.WikiDatabases.CountAsync()).Should().Be(1);
        (await fixture.Db.WikiDatabaseRows.CountAsync()).Should().Be(1);

        var page = await fixture.Db.WikiPages.SingleAsync();
        page.ContentVersion.Should().Be(2);
        WikiBlockJson.ParseBlocks(page.BlocksJson).Should().Contain(block =>
            block.PlainText == "Second edition");
        var revision = await fixture.Db.WikiPageRevisions.SingleAsync();
        revision.Label.Should().Be("Before Notion workspace import");
        WikiBlockJson.ParseBlocks(revision.BlocksJson).Should().Contain(block =>
            block.PlainText == "First edition");

        var database = await fixture.Db.WikiDatabases
            .Include(item => item.Properties)
            .Include(item => item.Rows)
            .SingleAsync();
        var email = database.Properties.Single(property => property.Name == "Email");
        WikiPropertyValues.GetText(
            WikiPropertyValues.ParseObject(database.Rows.Single().PropertyValuesJson),
            email.Id).Should().Be("new@example.test");
    }

    [Fact]
    public async Task ImportAsync_ShouldReconcileAnExistingApiSyncedPageByNotionId()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string exportId = "1234567890abcdef1234567890abcdef";
        var existing = new WikiPage
        {
            Title = "API page",
            Slug = "api-page",
            NotionId = "12345678-90ab-cdef-1234-567890abcdef",
            BlocksJson = "[]"
        };
        fixture.Db.WikiPages.Add(existing);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.ImportAsync(
            CreateArchive(($"Complete Page {exportId}.md", "# Complete Page\n\nFull exported content")),
            "Owner");

        result.PagesCreated.Should().Be(0);
        result.PagesUpdated.Should().Be(1);
        (await fixture.Db.WikiPages.CountAsync()).Should().Be(1);
        existing.NotionExportId.Should().Be(exportId);
        existing.Title.Should().Be("Complete Page");
        WikiBlockJson.ParseBlocks(existing.BlocksJson).Should().Contain(block =>
            block.PlainText == "Full exported content");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ApplicationDbContext Db { get; }
        public SentinelWorkspaceImportService Service { get; }

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            Service = new SentinelWorkspaceImportService(db);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static byte[] CreateArchive(
        params (string Name, object Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var destination = entry.Open();
                var bytes = content is byte[] binary
                    ? binary
                    : Encoding.UTF8.GetBytes((string)content);
                destination.Write(bytes);
            }
        }
        return stream.ToArray();
    }
}
