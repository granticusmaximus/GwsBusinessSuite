using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class PageRevisionServiceTests
{
    [Fact]
    public async Task CreateRevisionAsync_ShouldSnapshotThePage()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var revisions = new PageRevisionService(db);
        var page = await CreatePageAsync(cms, "[{\"type\":\"hero\",\"title\":\"Original\"}]");

        await revisions.CreateRevisionAsync(page, "first checkpoint");

        var list = await revisions.ListAsync(page.Id);
        list.Should().HaveCount(1);
        list[0].BlocksJson.Should().Contain("Original");
        list[0].Label.Should().Be("first checkpoint");
        list[0].RevisionNumber.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnNewestRevisionFirst()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var revisions = new PageRevisionService(db);
        var page = await CreatePageAsync(cms, "[]");

        await revisions.CreateRevisionAsync(page, "a");
        await revisions.CreateRevisionAsync(page, "b");
        await revisions.CreateRevisionAsync(page, "c");

        var list = await revisions.ListAsync(page.Id);
        list.Should().HaveCount(3);
        list[0].Label.Should().Be("c");
        list[0].RevisionNumber.Should().Be(3);
    }

    [Fact]
    public async Task RestoreAsync_ShouldCopyRevisionContentAndSaveCheckpointFirst()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var revisions = new PageRevisionService(db);
        var page = await CreatePageAsync(cms, "[{\"type\":\"hero\",\"title\":\"V1\"}]");

        var rev = await revisions.CreateRevisionAsync(page);

        // Update page to V2
        await cms.SavePageAsync(new CmsPageEditorModel
        {
            PageId = page.Id,
            SiteId = page.SiteId,
            Title = page.Title,
            BlocksJson = "[{\"type\":\"hero\",\"title\":\"V2\"}]"
        });

        // Restore to V1
        var restored = await revisions.RestoreAsync(page.Id, rev.Id);

        restored.BlocksJson.Should().Contain("V1");

        // A checkpoint of V2 was auto-saved before restore
        var list = await revisions.ListAsync(page.Id);
        list.Should().HaveCount(2);
        list[0].Label.Should().Contain("Auto-save");
    }

    [Fact]
    public async Task TrimOldRevisions_ShouldKeepOnlyMaxRevisions()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var revisions = new PageRevisionService(db);
        var page = await CreatePageAsync(cms, "[]");

        // Create 22 revisions (2 over the 20-revision limit)
        for (var i = 0; i < 22; i++)
        {
            await revisions.CreateRevisionAsync(page, $"rev {i + 1}");
        }

        var list = await revisions.ListAsync(page.Id);
        list.Should().HaveCount(20);
        list[0].Label.Should().Be("rev 22");   // newest first
        list[^1].Label.Should().Be("rev 3");   // oldest two trimmed
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveJustThatRevision()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var revisions = new PageRevisionService(db);
        var page = await CreatePageAsync(cms, "[]");
        var r1 = await revisions.CreateRevisionAsync(page, "keep");
        var r2 = await revisions.CreateRevisionAsync(page, "delete-me");

        await revisions.DeleteAsync(r2.Id);

        var list = await revisions.ListAsync(page.Id);
        list.Should().HaveCount(1);
        list[0].Id.Should().Be(r1.Id);
    }

    [Fact]
    public async Task DeletingPage_ShouldCascadeToRevisions()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var revisions = new PageRevisionService(db);
        var page = await CreatePageAsync(cms, "[]");
        await revisions.CreateRevisionAsync(page);
        await revisions.CreateRevisionAsync(page);

        await cms.TrashPageAsync(page.Id);
        await cms.DeletePageAsync(page.Id);

        (await revisions.ListAsync(page.Id)).Should().BeEmpty();
    }

    private static async Task<GwsBusinessSuite.Domain.Entities.CmsPage> CreatePageAsync(CmsBuilderService cms, string blocksJson)
    {
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Test" });
        return await cms.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Home",
            BlocksJson = blocksJson
        });
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
