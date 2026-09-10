using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// A recurring row points at a WikiDatabaseRowTemplate and materializes it again on a cron
// schedule via the real WikiDatabaseService.CreateRowFromTemplateAsync - the same operation
// "New row" from a template already performs by hand, so nothing here duplicates that logic.
public sealed class WikiDatabaseRecurrenceServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldComputeTheFirstOccurrence_AndRejectAnUnknownTemplate()
    {
        await using var db = await CreateDbAsync();
        var wikiDatabaseService = new WikiDatabaseService(db);
        var time = new FakeTimeProvider { UtcNow = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero) };
        var service = new WikiDatabaseRecurrenceService(db, wikiDatabaseService, time);
        var database = await wikiDatabaseService.CreateDatabaseAsync("Reports", null, "u");

        var missingTemplateAct = () => service.CreateAsync(database.Id, Guid.NewGuid(), "0 9 * * 1", "u");
        await missingTemplateAct.Should().ThrowAsync<InvalidOperationException>();

        var template = await CreateTemplateAsync(wikiDatabaseService, database.Id);
        var recurrence = await service.CreateAsync(database.Id, template.Id, "0 9 * * 1", "u");

        recurrence.NextRunAt.Should().Be(new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero), "the next Monday 9am after a Thursday");
        recurrence.IsActive.Should().BeTrue();
        recurrence.RowTemplateName.Should().Be(template.Name);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectAMalformedCronExpression()
    {
        await using var db = await CreateDbAsync();
        var wikiDatabaseService = new WikiDatabaseService(db);
        var service = new WikiDatabaseRecurrenceService(db, wikiDatabaseService, TimeProvider.System);
        var database = await wikiDatabaseService.CreateDatabaseAsync("Reports", null, "u");
        var template = await CreateTemplateAsync(wikiDatabaseService, database.Id);

        var act = () => service.CreateAsync(database.Id, template.Id, "not a cron expression", "u");

        await act.Should().ThrowAsync<FormatException>();
        (await db.WikiDatabaseRowRecurrences.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunDueAsync_ShouldCreateARowAndAdvanceNextRunAt_ForADueActiveRecurrence()
    {
        await using var db = await CreateDbAsync();
        var wikiDatabaseService = new WikiDatabaseService(db);
        var time = new FakeTimeProvider { UtcNow = new DateTimeOffset(2026, 9, 14, 8, 59, 0, TimeSpan.Zero) };
        var service = new WikiDatabaseRecurrenceService(db, wikiDatabaseService, time);
        var database = await wikiDatabaseService.CreateDatabaseAsync("Reports", null, "u");
        var template = await CreateTemplateAsync(wikiDatabaseService, database.Id);
        var recurrence = await service.CreateAsync(database.Id, template.Id, "0 9 * * 1", "u");

        time.UtcNow = new DateTimeOffset(2026, 9, 14, 9, 0, 30, TimeSpan.Zero);
        var created = await service.RunDueAsync();

        created.Should().Be(1);
        var reloaded = await db.WikiDatabaseRows.AsNoTracking().Where(row => row.WikiDatabaseId == database.Id).ToListAsync();
        reloaded.Should().ContainSingle(row => row.CreatedBy == "recurrence-scheduler");

        var storedRecurrence = await db.WikiDatabaseRowRecurrences.AsNoTracking().SingleAsync(item => item.Id == recurrence.Id);
        storedRecurrence.NextRunAt.Should().Be(new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero), "the following Monday");
        storedRecurrence.LastRunAt.Should().Be(time.UtcNow);
    }

    [Fact]
    public async Task RunDueAsync_ShouldNeverFireTwice_ForOneOccurrence_EvenIfCalledRepeatedlyAtTheSameMoment()
    {
        // The whole point of advancing NextRunAt before materializing the row (see
        // WikiDatabaseRecurrenceService.RunDueAsync's own comment) - a second sweep at the same
        // "now" must see the recurrence as no longer due.
        await using var db = await CreateDbAsync();
        var wikiDatabaseService = new WikiDatabaseService(db);
        var time = new FakeTimeProvider { UtcNow = new DateTimeOffset(2026, 9, 14, 8, 59, 0, TimeSpan.Zero) };
        var service = new WikiDatabaseRecurrenceService(db, wikiDatabaseService, time);
        var database = await wikiDatabaseService.CreateDatabaseAsync("Reports", null, "u");
        var template = await CreateTemplateAsync(wikiDatabaseService, database.Id);
        await service.CreateAsync(database.Id, template.Id, "0 9 * * 1", "u");
        time.UtcNow = new DateTimeOffset(2026, 9, 14, 9, 0, 30, TimeSpan.Zero);

        var firstSweep = await service.RunDueAsync();
        var secondSweep = await service.RunDueAsync();

        firstSweep.Should().Be(1);
        secondSweep.Should().Be(0);
        (await db.WikiDatabaseRows.CountAsync(row => row.CreatedBy == "recurrence-scheduler")).Should().Be(1);
    }

    [Fact]
    public async Task RunDueAsync_ShouldSkipAnInactiveRecurrence()
    {
        await using var db = await CreateDbAsync();
        var wikiDatabaseService = new WikiDatabaseService(db);
        var time = new FakeTimeProvider { UtcNow = new DateTimeOffset(2026, 9, 14, 8, 59, 0, TimeSpan.Zero) };
        var service = new WikiDatabaseRecurrenceService(db, wikiDatabaseService, time);
        var database = await wikiDatabaseService.CreateDatabaseAsync("Reports", null, "u");
        var template = await CreateTemplateAsync(wikiDatabaseService, database.Id);
        var recurrence = await service.CreateAsync(database.Id, template.Id, "0 9 * * 1", "u");
        await service.SetActiveAsync(recurrence.Id, false, "u");

        time.UtcNow = new DateTimeOffset(2026, 9, 14, 9, 0, 30, TimeSpan.Zero);
        var created = await service.RunDueAsync();

        created.Should().Be(0);
        (await db.WikiDatabaseRows.CountAsync(row => row.CreatedBy == "recurrence-scheduler")).Should().Be(0);
    }

    [Fact]
    public async Task SetActiveAsync_ShouldRecomputeNextRunAtFromNow_WhenReenabled_RatherThanCatchingUp()
    {
        await using var db = await CreateDbAsync();
        var wikiDatabaseService = new WikiDatabaseService(db);
        var time = new FakeTimeProvider { UtcNow = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero) };
        var service = new WikiDatabaseRecurrenceService(db, wikiDatabaseService, time);
        var database = await wikiDatabaseService.CreateDatabaseAsync("Reports", null, "u");
        var template = await CreateTemplateAsync(wikiDatabaseService, database.Id);
        var recurrence = await service.CreateAsync(database.Id, template.Id, "0 9 * * 1", "u");
        await service.SetActiveAsync(recurrence.Id, false, "u");

        // Time passes well beyond the original NextRunAt while it sits disabled.
        time.UtcNow = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        await service.SetActiveAsync(recurrence.Id, true, "u");

        var reenabled = (await service.ListAsync(database.Id)).Single();
        reenabled.NextRunAt.Should().Be(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero), "the next Monday from the re-enable moment, not a missed occurrence");
    }

    [Fact]
    public async Task RunDueAsync_ShouldSkipAndLog_WhenARecurrencesOwnTemplateHasBeenDeleted_WithoutBlockingOthers()
    {
        await using var db = await CreateDbAsync();
        var wikiDatabaseService = new WikiDatabaseService(db);
        var time = new FakeTimeProvider { UtcNow = new DateTimeOffset(2026, 9, 14, 8, 59, 0, TimeSpan.Zero) };
        var service = new WikiDatabaseRecurrenceService(db, wikiDatabaseService, time);
        var database = await wikiDatabaseService.CreateDatabaseAsync("Reports", null, "u");
        var brokenTemplate = await CreateTemplateAsync(wikiDatabaseService, database.Id, "Broken");
        var healthyTemplate = await CreateTemplateAsync(wikiDatabaseService, database.Id, "Healthy");
        await service.CreateAsync(database.Id, brokenTemplate.Id, "0 9 * * 1", "u");
        await service.CreateAsync(database.Id, healthyTemplate.Id, "0 9 * * 1", "u");
        await wikiDatabaseService.DeleteRowTemplateAsync(database.Id, brokenTemplate.Id);

        time.UtcNow = new DateTimeOffset(2026, 9, 14, 9, 0, 30, TimeSpan.Zero);
        var created = await service.RunDueAsync();

        created.Should().Be(1, "the healthy recurrence must still fire despite the broken one failing");
    }

    private static async Task<WikiDatabaseRowTemplate> CreateTemplateAsync(
        IWikiDatabaseService wikiDatabaseService, Guid wikiDatabaseId, string name = "Weekly report")
    {
        var sourceRow = await wikiDatabaseService.SaveRowAsync(wikiDatabaseId, new WikiDatabaseRowEditor(), "u");
        return await wikiDatabaseService.CreateRowTemplateFromRowAsync(wikiDatabaseId, sourceRow.Id, name, "u");
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

    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
