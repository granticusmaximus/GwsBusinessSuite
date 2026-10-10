using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelReminderServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 16, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public async Task CreateAsync_ShouldScheduleForTheSignedInUser_AtNineAmForAWholeDay()
    {
        var (db, _, service, page, block) = await CreateAsync();

        var reminder = await service.CreateAsync(page.Id, block, "2026-10-12", "12-10-26", "Grant");

        reminder.State.Should().Be(SentinelReminderStates.Scheduled);
        reminder.DueAt.Should().Be(new DateTimeOffset(2026, 10, 12, 13, 0, 0, TimeSpan.Zero));
        (await db.PageReminders.SingleAsync()).OwnerUsername.Should().Be("grant");
        (await service.ListScheduledForPageAsync(page.Id, "grant")).Should().ContainSingle();
        (await service.ListScheduledForPageAsync(page.Id, "sam")).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldNotDuplicateTheSameReminder()
    {
        var (db, _, service, page, block) = await CreateAsync();

        var first = await service.CreateAsync(page.Id, block, "2026-10-12T19:00:00Z", "12-10-26 15:00", "grant");
        var second = await service.CreateAsync(page.Id, block, "2026-10-12T19:00:00Z", "12-10-26 15:00", "grant");

        second.Id.Should().Be(first.Id);
        (await db.PageReminders.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("2026-10-09T15:00:00Z", "already passed")]
    [InlineData("not-a-date", "recognise")]
    public async Task CreateAsync_ShouldRefusePastOrUnreadableTimes(string value, string expected)
    {
        var (_, _, service, page, block) = await CreateAsync();

        var act = () => service.CreateAsync(page.Id, block, value, "x", "grant");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(expected);
    }

    [Fact]
    public async Task CreateAsync_ShouldRefuseABlockThatIsNotOnThePage_AndAPageTheUserCannotView()
    {
        var (db, _, service, page, _) = await CreateAsync();
        var missingBlock = () => service.CreateAsync(page.Id, Guid.NewGuid(), "2026-10-12", "x", "grant");
        await missingBlock.Should().ThrowAsync<InvalidOperationException>();

        var (_, _, _, privatePage, privateBlock) = await AddPageAsync(db, "Private");
        db.SentinelResourcePermissions.Add(new SentinelResourcePermission
        {
            TargetId = privatePage.Id, IsDatabase = false, Username = "grant", AccessLevel = SentinelAccessLevels.FullAccess
        });
        await db.SaveChangesAsync();
        var noAccess = () => service.CreateAsync(privatePage.Id, privateBlock, "2026-10-12", "x", "sam");
        await noAccess.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SendDueAsync_ShouldRingTheBellExactlyOnce_WithPageAndBlock()
    {
        var (db, time, service, page, block) = await CreateAsync();
        await service.CreateAsync(page.Id, block, "2026-10-09T17:00:00Z", "09-10-26 13:00", "grant");

        (await service.SendDueAsync()).Should().Be(0, "it isn't due yet");
        time.UtcNow = Now.AddHours(2);
        (await service.SendDueAsync()).Should().Be(1);
        (await service.SendDueAsync()).Should().Be(0);
        time.UtcNow = Now.AddDays(3);
        (await service.SendDueAsync()).Should().Be(0);

        var bell = await db.SentinelNotifications.SingleAsync();
        bell.Username.Should().Be("grant");
        bell.Kind.Should().Be(SentinelReminderService.NotificationKind);
        bell.WikiPageId.Should().Be(page.Id);
        bell.WikiBlockId.Should().Be(block);
        bell.Message.Should().Contain("09-10-26 13:00").And.Contain("Plans");
        (await service.ListScheduledForPageAsync(page.Id, "grant")).Should().BeEmpty();
    }

    [Fact]
    public async Task SendDueAsync_ShouldSkipCancelledReminders_AndOnlyTheOwnerCanCancel()
    {
        var (db, time, service, page, block) = await CreateAsync();
        var reminder = await service.CreateAsync(page.Id, block, "2026-10-09T17:00:00Z", "x", "grant");

        await service.CancelAsync(reminder.Id, "sam");
        (await db.PageReminders.AsNoTracking().SingleAsync()).CancelledUnix.Should().BeNull();
        await service.CancelAsync(reminder.Id, "grant");

        time.UtcNow = Now.AddHours(2);
        (await service.SendDueAsync()).Should().Be(0);
        (await db.SentinelNotifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SendDueAsync_ShouldCancelRatherThanSend_WhenThePageIsTrashedOrAccessIsGone()
    {
        var (db, time, service, page, block) = await CreateAsync();
        var (_, _, _, shared, sharedBlock) = await AddPageAsync(db, "Shared");
        var grant = new SentinelResourcePermission
        {
            TargetId = shared.Id, IsDatabase = false, Username = "sam", AccessLevel = SentinelAccessLevels.View
        };
        db.SentinelResourcePermissions.Add(grant);
        await db.SaveChangesAsync();
        await service.CreateAsync(page.Id, block, "2026-10-09T17:00:00Z", "x", "grant");
        await service.CreateAsync(shared.Id, sharedBlock, "2026-10-09T17:00:00Z", "x", "sam");

        // Grant's page is trashed and Sam's access to the shared page is taken away.
        page.TrashedAt = Now;
        db.SentinelResourcePermissions.Remove(grant);
        await db.SaveChangesAsync();

        time.UtcNow = Now.AddHours(2);
        (await service.SendDueAsync()).Should().Be(0);
        (await db.SentinelNotifications.CountAsync()).Should().Be(0);
        (await db.PageReminders.AsNoTracking().ToListAsync()).Should().OnlyContain(reminder => reminder.CancelledUnix != null);
    }

    private static async Task<(ApplicationDbContext Db, FakeTimeProvider Time, SentinelReminderService Service, WikiPage Page, Guid Block)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        var time = new FakeTimeProvider { UtcNow = Now };
        var service = new SentinelReminderService(db, time, new SentinelAccessService(db));
        var (_, _, _, page, block) = await AddPageAsync(db, "Plans");
        return (db, time, service, page, block);
    }

    private static async Task<(ApplicationDbContext, FakeTimeProvider?, SentinelReminderService?, WikiPage, Guid)> AddPageAsync(ApplicationDbContext db, string title)
    {
        var block = Guid.NewGuid();
        var page = new WikiPage
        {
            Title = title,
            Slug = title.ToLowerInvariant(),
            BlocksJson = WikiBlockJson.Serialize([new WikiBlock(block, WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan("Ship")], new Dictionary<string, string>())])
        };
        db.WikiPages.Add(page);
        await db.SaveChangesAsync();
        return (db, null, null, page, block);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override TimeZoneInfo LocalTimeZone => NewYork;
    }
}
