using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class GlobalBlockServiceTests
{
    [Fact]
    public async Task ListAsync_ShouldOrderSectionsBeforeWidgets_ThenByName()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Globals" });

        await globals.CreateWidgetAsync(site.Id, "Zebra widget", new LayoutWidget { Id = "w1", WidgetType = "paragraph" });
        await globals.CreateSectionAsync(site.Id, "Alpha section", new LayoutSection { Id = "s1" });

        var list = await globals.ListAsync(site.Id);

        list.Should().HaveCount(2);
        list[0].Kind.Should().Be(GlobalBlockKinds.Section);
        list[1].Kind.Should().Be(GlobalBlockKinds.Widget);
    }

    [Fact]
    public async Task RenameAsync_ShouldUpdateName()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Globals" });

        var block = await globals.CreateWidgetAsync(site.Id, "Original name", new LayoutWidget { Id = "w1", WidgetType = "paragraph" });

        await globals.RenameAsync(site.Id, block.Id, "  New name  ");

        var list = await globals.ListAsync(site.Id);
        list.Single().Name.Should().Be("New name");
    }

    [Fact]
    public async Task RenameAsync_ShouldThrow_WhenBlockBelongsToDifferentSite()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site A" });
        var otherSite = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Site B" });

        var block = await globals.CreateWidgetAsync(site.Id, "Original name", new LayoutWidget { Id = "w1", WidgetType = "paragraph" });

        var act = () => globals.RenameAsync(otherSite.Id, block.Id, "Hijacked name");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SetOverridableFieldsAsync_ShouldOnlyPersistValidCandidateFields()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Globals" });
        var block = await globals.CreateWidgetAsync(site.Id, "Product card", new LayoutWidget { Id = "w1", WidgetType = "card" });

        // "variant" isn't in GlobalBlockOverridableFields.CandidatesFor("card") - should be
        // silently dropped rather than accepted, since the Inspector only ever offers real
        // candidates and a stray key here would just be dead weight.
        await globals.SetOverridableFieldsAsync(site.Id, block.Id, ["title", "variant"]);

        var reloaded = (await globals.ListAsync(site.Id)).Single();
        GlobalBlockOverridableFields.Parse(reloaded.OverridableFieldsJson).Should().BeEquivalentTo(["title"]);
    }

    [Fact]
    public async Task SetOverridableFieldsAsync_ShouldThrow_ForASectionKindBlock()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Globals" });
        var block = await globals.CreateSectionAsync(site.Id, "Hero strip", new LayoutSection { Id = "s1" });

        var act = () => globals.SetOverridableFieldsAsync(site.Id, block.Id, ["title"]);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SyncWidgetAsync_ShouldPreserveTheCanonicalValue_ForAnOverridableField()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Globals" });

        var block = await globals.CreateWidgetAsync(site.Id, "Product card", new LayoutWidget
        {
            Id = "w1",
            WidgetType = "card",
            Props = new Dictionary<string, string> { ["title"] = "Shared title", ["body"] = "Shared body" }
        });
        await globals.SetOverridableFieldsAsync(site.Id, block.Id, ["title"]);

        // Simulates what the resolver merge already produced in memory before this placement's
        // save: Props["title"] holds THIS placement's own resolved value (via Overrides), not
        // the canonical's - syncing must not let that leak back into the shared block.
        var placement = new LayoutWidget
        {
            Id = "placement-1",
            GlobalBlockId = block.Id,
            WidgetType = "card",
            Props = new Dictionary<string, string> { ["title"] = "This placement's own title", ["body"] = "Edited shared body" },
            Overrides = new Dictionary<string, string> { ["title"] = "This placement's own title" }
        };

        var synced = await globals.SyncWidgetAsync(site.Id, placement);
        var canonical = CmsBuilderJson.Parse<LayoutWidget>(synced.Json);

        canonical!.Props["title"].Should().Be("Shared title", "title is overridable - no placement's save should change the shared default");
        canonical.Props["body"].Should().Be("Edited shared body", "body was never flagged overridable, so it still syncs normally");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveBlock_FromListAsync()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Globals" });

        var block = await globals.CreateWidgetAsync(site.Id, "Doomed widget", new LayoutWidget { Id = "w1", WidgetType = "paragraph" });

        await globals.DeleteAsync(site.Id, block.Id);

        (await globals.ListAsync(site.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_ShouldBeNoOp_WhenBlockAlreadyDeletedOrMissing()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Globals" });

        var act = () => globals.DeleteAsync(site.Id, Guid.NewGuid());

        await act.Should().NotThrowAsync();
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
