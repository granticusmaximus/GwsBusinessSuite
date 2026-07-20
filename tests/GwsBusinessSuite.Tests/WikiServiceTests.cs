using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class WikiServiceTests
{
    [Fact]
    public async Task SavePageAsync_ShouldCreateUpdateAndDeleteWikiPages()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Internal Runbook",
            BlocksJson = ParagraphBlocks("Steps go here.")
        }, "grantwatson");

        created.Slug.Should().Be("internal-runbook");
        created.BlocksJson.Should().Contain("Steps go here.");
        created.CreatedBy.Should().Be("grantwatson");

        var listed = await service.ListPagesAsync();
        listed.Should().HaveCount(1);

        var loaded = await service.GetPageAsync(created.Id);
        loaded.Should().NotBeNull();
        loaded!.Title.Should().Be("Internal Runbook");

        var updated = await service.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = created.Id,
            Title = "Internal Runbook",
            BlocksJson = ParagraphBlocks("Updated steps.")
        }, "grantwatson");

        updated.Id.Should().Be(created.Id);
        updated.BlocksJson.Should().Contain("Updated steps.");
        updated.UpdatedAt.Should().NotBeNull();
        updated.UpdatedBy.Should().Be("grantwatson");

        await service.DeletePageAsync(created.Id, "grantwatson");

        (await service.ListPagesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SavePageAsync_ShouldGenerateUniqueSlugsForDuplicateTitles()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiService(db);

        var first = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Team Notes",
            BlocksJson = ParagraphBlocks("First page")
        }, "grantwatson");

        var second = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Team Notes",
            BlocksJson = ParagraphBlocks("Second page")
        }, "grantwatson");

        first.Slug.Should().Be("team-notes");
        second.Slug.Should().Be("team-notes-2");
    }

    [Fact]
    public async Task SavePageAsync_ShouldCreateOneRevisionPerSave_AndDiffShouldShowTheChange()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Deploy Steps",
            BlocksJson = ParagraphBlocks("Step one.")
        }, "grantwatson");

        await service.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = created.Id,
            Title = "Deploy Steps",
            BlocksJson = ParagraphBlocks("Step two.")
        }, "grantwatson");

        var history = await service.GetHistoryAsync(created.Id);
        history.Should().HaveCount(2);
        history[0].RevisionNumber.Should().Be(2, "history is newest-first");
        history[1].RevisionNumber.Should().Be(1);
        history.Should().OnlyContain(entry => entry.AuthorName == "grantwatson");

        var diff = await service.GetStructuralDiffAsync(created.Id, history[1].Id, history[0].Id);
        diff.Should().NotBeNullOrEmpty();
        diff.Should().Contain("Step two.");
    }

    [Fact]
    public async Task SavePageAsync_ShouldTrimOldRevisions_BeyondMaxRevisionsPerPage()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Frequently Edited",
            BlocksJson = ParagraphBlocks("v0")
        }, "grantwatson");

        for (var i = 1; i <= 25; i++)
        {
            await service.SavePageAsync(new WikiPageEditorModel
            {
                WikiPageId = created.Id,
                Title = "Frequently Edited",
                BlocksJson = ParagraphBlocks($"v{i}")
            }, "grantwatson");
        }

        var history = await service.GetHistoryAsync(created.Id);
        history.Should().HaveCount(20, "revisions are trimmed to WikiService.MaxRevisionsPerPage");
        history[0].RevisionNumber.Should().Be(26, "the newest revisions are kept, not the oldest");
    }

    [Fact]
    public async Task RevertToRevisionAsync_ShouldRestoreOldContent_AsANewVersion()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Onboarding",
            BlocksJson = ParagraphBlocks("Version one.")
        }, "grantwatson");
        var firstRevisionId = (await service.GetHistoryAsync(created.Id))[0].Id;

        await service.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = created.Id,
            Title = "Onboarding",
            BlocksJson = ParagraphBlocks("Version two.")
        }, "grantwatson");

        var reverted = await service.RevertToRevisionAsync(created.Id, firstRevisionId, "grantwatson");

        reverted.BlocksJson.Should().Contain("Version one.");

        var history = await service.GetHistoryAsync(created.Id);
        history.Should().HaveCount(3, "the revert itself is a new version, not a history rewrite");
    }

    [Fact]
    public async Task SavePageAsync_ShouldSetParentOnlyAtCreation_NotOnSubsequentContentSaves()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiService(db);

        var parent = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Parent Page",
            BlocksJson = ParagraphBlocks("Parent content.")
        }, "grantwatson");

        var child = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Child Page",
            BlocksJson = ParagraphBlocks("Child content."),
            ParentWikiPageId = parent.Id
        }, "grantwatson");

        child.ParentWikiPageId.Should().Be(parent.Id);

        // A content-only save with a different ParentWikiPageId in the editor model must not
        // silently re-parent an existing page - that would leave stale/colliding SortOrder
        // values under the new parent since only ReorderPageAsync renumbers siblings.
        var resaved = await service.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = child.Id,
            Title = "Child Page",
            BlocksJson = ParagraphBlocks("Child content, edited."),
            ParentWikiPageId = null
        }, "grantwatson");

        resaved.ParentWikiPageId.Should().Be(parent.Id, "content saves must not change the parent");
    }

    [Fact]
    public async Task ReorderPageAsync_ShouldChangeParentAndRenumberSiblings()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiService(db);

        var oldParent = await service.SavePageAsync(new WikiPageEditorModel { Title = "Old Parent", BlocksJson = ParagraphBlocks("x") }, "u");
        var newParent = await service.SavePageAsync(new WikiPageEditorModel { Title = "New Parent", BlocksJson = ParagraphBlocks("x") }, "u");
        var child = await service.SavePageAsync(new WikiPageEditorModel { Title = "Child", BlocksJson = ParagraphBlocks("x"), ParentWikiPageId = oldParent.Id }, "u");
        var sibling = await service.SavePageAsync(new WikiPageEditorModel { Title = "Existing Under New Parent", BlocksJson = ParagraphBlocks("x"), ParentWikiPageId = newParent.Id }, "u");

        await service.ReorderPageAsync(child.Id, newParent.Id, 0, "u");

        var moved = await service.GetPageAsync(child.Id);
        moved!.ParentWikiPageId.Should().Be(newParent.Id);
        moved.SortOrder.Should().Be(0, "inserted at index 0");

        var movedSibling = await service.GetPageAsync(sibling.Id);
        movedSibling!.SortOrder.Should().Be(1, "the existing child was pushed to index 1");
    }

    [Fact]
    public async Task ReorderPageAsync_ShouldRejectSelfParenting()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiService(db);

        var page = await service.SavePageAsync(new WikiPageEditorModel { Title = "Page", BlocksJson = ParagraphBlocks("x") }, "u");

        var act = () => service.ReorderPageAsync(page.Id, page.Id, 0, "u");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ReorderPageAsync_ShouldRejectMovingAPageUnderItsOwnDescendant()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiService(db);

        var grandparent = await service.SavePageAsync(new WikiPageEditorModel { Title = "Grandparent", BlocksJson = ParagraphBlocks("x") }, "u");
        var parent = await service.SavePageAsync(new WikiPageEditorModel { Title = "Parent", BlocksJson = ParagraphBlocks("x"), ParentWikiPageId = grandparent.Id }, "u");
        var child = await service.SavePageAsync(new WikiPageEditorModel { Title = "Child", BlocksJson = ParagraphBlocks("x"), ParentWikiPageId = parent.Id }, "u");

        var act = () => service.ReorderPageAsync(grandparent.Id, child.Id, 0, "u");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeletePageAsync_ShouldCascadeDeleteRevisions()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Temp Page",
            BlocksJson = ParagraphBlocks("Temporary content.")
        }, "grantwatson");

        await service.DeletePageAsync(created.Id, "grantwatson");

        (await db.WikiPageRevisions.Where(r => r.WikiPageId == created.Id).ToListAsync()).Should().BeEmpty();
    }

    private static string ParagraphBlocks(string text) => WikiBlockJson.Serialize(
    [
        new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string>())
    ]);

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
