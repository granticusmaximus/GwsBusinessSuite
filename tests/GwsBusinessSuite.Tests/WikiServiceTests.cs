using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
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
            Markdown = "# Internal Runbook\n\nSteps go here."
        });

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
        });

        updated.Id.Should().Be(created.Id);
        updated.Markdown.Should().Contain("Updated steps.");
        updated.UpdatedAt.Should().NotBeNull();

        await service.DeletePageAsync(created.Id);

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
            Markdown = "First page"
        });

        var second = await service.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Team Notes",
            Markdown = "Second page"
        });

        first.Slug.Should().Be("team-notes");
        second.Slug.Should().Be("team-notes-2");
    }

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
