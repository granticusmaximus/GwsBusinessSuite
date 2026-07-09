using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Git operations here are local and deterministic (no live external dependency, unlike
// SSH), so this exercises the real WikiService against a real temp git repo per test
// rather than mocking anything out.
public sealed class WikiServiceTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"gws-wiki-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task SavePageAsync_ShouldCreateUpdateAndDeleteWikiPages()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Internal Runbook",
            Markdown = "# Internal Runbook\n\nSteps go here."
        }, "grantwatson");

        created.Slug.Should().Be("internal-runbook");
        created.Markdown.Should().Contain("Steps go here.");
        created.CreatedBy.Should().Be("wiki-ui");

        var listed = await service.ListPagesAsync();
        listed.Should().HaveCount(1);

        var loaded = await service.GetPageAsync(created.Id);
        loaded.Should().NotBeNull();
        loaded!.Title.Should().Be("Internal Runbook");

        var updated = await service.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = created.Id,
            Title = "Internal Runbook",
            Markdown = "# Internal Runbook\n\nUpdated steps."
        }, "grantwatson");

        updated.Id.Should().Be(created.Id);
        updated.Markdown.Should().Contain("Updated steps.");
        updated.UpdatedAt.Should().NotBeNull();

        await service.DeletePageAsync(created.Id, "grantwatson");

        (await service.ListPagesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SavePageAsync_ShouldGenerateUniqueSlugsForDuplicateTitles()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var first = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Team Notes",
            Markdown = "First page"
        }, "grantwatson");

        var second = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Team Notes",
            Markdown = "Second page"
        }, "grantwatson");

        first.Slug.Should().Be("team-notes");
        second.Slug.Should().Be("team-notes-2");
    }

    [Fact]
    public async Task SavePageAsync_ShouldCreateOneCommitPerSave_AndDiffShouldShowTheChange()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Deploy Steps",
            Markdown = "Step one."
        }, "grantwatson");

        var updated = await service.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = created.Id,
            Title = "Deploy Steps",
            Markdown = "Step one.\nStep two."
        }, "grantwatson");

        var history = await service.GetHistoryAsync(created.Id);
        history.Should().HaveCount(2);
        history[0].Message.Should().Be("Update Deploy Steps");
        history[1].Message.Should().Be("Create Deploy Steps");
        history.Should().OnlyContain(entry => entry.AuthorName == "grantwatson");

        var diff = await service.GetDiffAsync(created.Id, history[1].Sha, history[0].Sha);
        diff.Should().NotBeNullOrEmpty();
        diff.Should().Contain("Step two.");
    }

    [Fact]
    public async Task SavePageAsync_WithoutContentChange_ShouldNotCreateAnEmptyCommit()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Static Page",
            Markdown = "Nothing ever changes here."
        }, "grantwatson");

        // Re-saving identical content should be a no-op on the git side.
        await service.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = created.Id,
            Title = "Static Page",
            Markdown = "Nothing ever changes here."
        }, "grantwatson");

        var history = await service.GetHistoryAsync(created.Id);
        history.Should().HaveCount(1);
    }

    [Fact]
    public async Task SavePageAsync_ShouldPreserveHistory_AcrossASlugRename()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Old Title",
            Markdown = "Original content."
        }, "grantwatson");

        var renamed = await service.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = created.Id,
            Title = "New Title",
            Slug = "new-title",
            Markdown = "Original content."
        }, "grantwatson");

        renamed.Slug.Should().Be("new-title");

        var history = await service.GetHistoryAsync(created.Id);
        history.Should().HaveCount(2);
        history.Select(h => h.Message).Should().Contain("Create Old Title");
    }

    [Fact]
    public async Task RevertToRevisionAsync_ShouldRestoreOldContent_AsANewCommit()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Onboarding",
            Markdown = "Version one."
        }, "grantwatson");
        var firstSha = (await service.GetHistoryAsync(created.Id))[0].Sha;

        await service.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = created.Id,
            Title = "Onboarding",
            Markdown = "Version two."
        }, "grantwatson");

        var reverted = await service.RevertToRevisionAsync(created.Id, firstSha, "grantwatson");

        reverted.Markdown.Should().Be("Version one.");

        var history = await service.GetHistoryAsync(created.Id);
        history.Should().HaveCount(3, "the revert itself should be a new commit, not a history rewrite");
        history[0].Message.Should().Be("Update Onboarding");
    }

    [Fact]
    public async Task DeletePageAsync_ShouldRemoveTheFileFromTheRepo()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var created = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Temp Page",
            Markdown = "Temporary content."
        }, "grantwatson");

        await service.DeletePageAsync(created.Id, "grantwatson");

        File.Exists(Path.Combine(_repoPath, "temp-page.md")).Should().BeFalse();
    }

    private WikiService CreateService(ApplicationDbContext db) => new(db, _repoPath);

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

    public void Dispose()
    {
        if (Directory.Exists(_repoPath))
        {
            // Git objects are written read-only on some platforms; normalize attributes
            // before deleting so cleanup doesn't throw.
            foreach (var file in Directory.EnumerateFiles(_repoPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repoPath, recursive: true);
        }
    }
}
