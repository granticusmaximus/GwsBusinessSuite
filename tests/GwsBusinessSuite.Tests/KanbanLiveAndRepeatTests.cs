using FluentAssertions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Boards K6: repeating cards' schedules, and the live-update save hook.
public sealed class KanbanLiveAndRepeatTests
{
    [Theory]
    [InlineData(KanbanRepeatFrequencies.Daily, 9, 0, 1, 1, "0 9 * * *", "Every day at 09:00")]
    [InlineData(KanbanRepeatFrequencies.Weekdays, 8, 30, 1, 1, "30 8 * * 1-5", "Every weekday at 08:30")]
    [InlineData(KanbanRepeatFrequencies.Weekly, 17, 5, 5, 1, "5 17 * * 5", "Every Friday at 17:05")]
    [InlineData(KanbanRepeatFrequencies.Monthly, 9, 0, 1, 22, "0 9 22 * *", "On the 22nd of every month at 09:00")]
    public void Repeats_ShouldRoundTripThroughCron(string frequency, int hour, int minute, int dayOfWeek, int dayOfMonth, string cron, string description)
    {
        var repeat = new KanbanRepeat(frequency, hour, minute, dayOfWeek, dayOfMonth);

        KanbanRepeats.ToCron(repeat).Should().Be(cron);
        KanbanRepeats.Describe(cron).Should().Be(description);
        KanbanRepeats.FromCron(cron).Should().Be(frequency switch
        {
            KanbanRepeatFrequencies.Weekly => repeat with { DayOfMonth = 1 },
            KanbanRepeatFrequencies.Monthly => repeat with { DayOfWeek = 1 },
            _ => repeat with { DayOfWeek = 1, DayOfMonth = 1 }
        });
        GwsBusinessSuite.Application.Automation.CronSchedule.Validate(cron);
    }

    [Theory]
    [InlineData("*/15 * * * *")]
    [InlineData("0 9 1,15 * *")]
    [InlineData("0 9 * 1 1")]
    [InlineData("")]
    public void Repeats_ShouldCallAnyOtherCronACustomSchedule(string cron)
    {
        KanbanRepeats.FromCron(cron).Should().BeNull();
        KanbanRepeats.Describe(cron).Should().Be("Custom schedule");
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(28, "28th")]
    public void Ordinal_ShouldReadLikeADate(int day, string expected) => KanbanRepeats.Ordinal(day).Should().Be(expected);

    [Fact]
    public async Task ChangeHook_ShouldPublishEachTouchedDatabase_AfterTheSave_WithItsContext()
    {
        var notifier = new WikiDatabaseChangeNotifier(TimeProvider.System);
        var heard = new List<WikiDatabaseChanged>();
        notifier.Changed += heard.Add;
        notifier.Changed += _ => throw new InvalidOperationException("a gone circuit");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new WikiDatabaseChangeInterceptor(notifier))
            .Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = new GwsBusinessSuite.Infrastructure.Services.WikiDatabaseService(db);
        var database = await service.CreateDatabaseAsync("Tasks", null, "grant");
        heard.Clear();

        var parent = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor(), "grant");
        var child = await service.SaveRowAsync(database.Id, new WikiDatabaseRowEditor { ParentRowId = parent.Id }, "sam");

        heard.Should().HaveCount(2);
        heard.Should().OnlyContain(change => change.WikiDatabaseId == database.Id && ReferenceEquals(change.Origin, db));
        heard[0].RowIds.Should().Equal(parent.Id);
        heard[0].Actor.Should().Be("grant");
        heard[1].RowIds.Should().Equal(child.Id);
        heard[1].ParentRowIds.Should().Equal(parent.Id);
        heard[1].Actor.Should().Be("sam");

        // Board settings count too; a save that touches no database publishes nothing.
        heard.Clear();
        db.KanbanBoardSettings.Add(new KanbanBoardSettings { WikiDatabaseId = database.Id, UpdatedBy = "grant" });
        await db.SaveChangesAsync();
        db.AppUsers.Add(new AppUser { Username = "nobody", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        heard.Should().ContainSingle().Which.RowIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangeHook_ShouldPublishNothing_WhenTheSaveFails()
    {
        var notifier = new WikiDatabaseChangeNotifier(TimeProvider.System);
        var heard = new List<WikiDatabaseChanged>();
        notifier.Changed += heard.Add;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new WikiDatabaseChangeInterceptor(notifier))
            .Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var database = await new GwsBusinessSuite.Infrastructure.Services.WikiDatabaseService(db).CreateDatabaseAsync("Tasks", null, "grant");
        var row = new WikiDatabaseRow { WikiDatabaseId = database.Id };
        db.WikiDatabaseRows.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        heard.Clear();

        // The same id again: the primary key refuses it, and the failed save tells no one.
        db.WikiDatabaseRows.Add(new WikiDatabaseRow { Id = row.Id, WikiDatabaseId = database.Id });
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
        db.ChangeTracker.Clear();
        db.AppUsers.Add(new AppUser { Username = "after", Role = AppRoles.Author });
        await db.SaveChangesAsync();

        heard.Should().BeEmpty();
    }
}
