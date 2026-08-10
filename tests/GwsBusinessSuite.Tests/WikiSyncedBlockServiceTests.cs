using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class WikiSyncedBlockServiceTests
{
    [Fact]
    public async Task CreateAsync_ThenUpdateContentAsync_ShouldBeReflectedByGetContentBatchAsync()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiSyncedBlockService(db);

        var sourceId = await service.CreateAsync(
            [new WikiRichTextSpan("Initial")], originWikiPageId: null, "grant");

        var initial = await service.GetContentBatchAsync([sourceId]);
        initial[sourceId].Should().ContainSingle(span => span.Text == "Initial");

        await service.UpdateContentAsync(sourceId, [new WikiRichTextSpan("Changed")], "someone-else");

        var updated = await service.GetContentBatchAsync([sourceId]);
        updated[sourceId].Should().ContainSingle(span => span.Text == "Changed");
    }

    [Fact]
    public async Task GetContentBatchAsync_ShouldReturnEveryRequestedSourceInOneCall()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiSyncedBlockService(db);

        var first = await service.CreateAsync([new WikiRichTextSpan("First")], null, "grant");
        var second = await service.CreateAsync([new WikiRichTextSpan("Second")], null, "grant");

        var batch = await service.GetContentBatchAsync([first, second, Guid.NewGuid()]);

        batch.Should().HaveCount(2);
        batch[first].Should().ContainSingle(span => span.Text == "First");
        batch[second].Should().ContainSingle(span => span.Text == "Second");
    }

    [Fact]
    public async Task UpdateContentAsync_ForAnUnknownSourceId_ShouldNotThrow()
    {
        await using var db = await CreateDbAsync();
        var service = new WikiSyncedBlockService(db);

        var act = () => service.UpdateContentAsync(Guid.NewGuid(), [new WikiRichTextSpan("x")], "grant");

        await act.Should().NotThrowAsync();
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
