using FluentAssertions;
using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// The bell's inbox (shell redesign R5c): done, snooze, and what shows.
public sealed class NotificationInboxTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Clock _clock = new(Now);
    private NotificationInboxStateService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        IDbContextFactory<ApplicationDbContext> factory = new Factory(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        _service = new NotificationInboxStateService(factory, _clock);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private static InboxItem Item(string kind, Guid id, DateTimeOffset at, bool read = false) =>
        new(kind, id, kind + " title", "message", read, at, "/admin");

    private static InboxItemState State(InboxItem item, DateTimeOffset? done = null, DateTimeOffset? snoozedAt = null, DateTimeOffset? until = null) =>
        new(item.Kind, item.Id, done?.ToUnixTimeMilliseconds(), snoozedAt?.ToUnixTimeMilliseconds(), until?.ToUnixTimeMilliseconds());

    [Fact]
    public void Done_ShouldHideAnAlert_ButAThreadComesBackWhenSomeoneWritesAgain()
    {
        var alert = Item("docker", Guid.NewGuid(), Now.AddHours(-2));
        var thread = Item("chat", Guid.NewGuid(), Now.AddHours(-1));
        var states = new[] { State(alert, done: Now.AddMinutes(-30)), State(thread, done: Now.AddMinutes(-30)) };

        NotificationInboxRules.Apply([alert, thread], states, Now).Visible.Should().BeEmpty();

        var newerMessage = thread with { ActivityAt = Now.AddMinutes(-5) };
        NotificationInboxRules.Apply([alert, newerMessage], states, Now).Visible.Select(e => e.Item.Kind).Should().Equal("chat");
    }

    [Fact]
    public void Snooze_ShouldHideUntilItsTime_ThenComeBackOnTop_AndLeaveTheBadge()
    {
        var older = Item("social", Guid.NewGuid(), Now.AddDays(-1));
        var newer = Item("docker", Guid.NewGuid(), Now.AddMinutes(-10));
        var states = new[] { State(older, snoozedAt: Now.AddHours(-3), until: Now.AddHours(1)) };

        var snoozedView = NotificationInboxRules.Apply([older, newer], states, Now);
        snoozedView.Visible.Select(e => e.Item.Id).Should().Equal(newer.Id);
        snoozedView.Snoozed.Should().ContainSingle().Which.SnoozedUntil.Should().Be(Now.AddHours(1));
        snoozedView.UnreadCount.Should().Be(1, "a snoozed item isn't in the badge");
        snoozedView.NextWake.Should().Be(Now.AddHours(1));

        var later = NotificationInboxRules.Apply([older, newer], states, Now.AddHours(2));
        later.Visible.First().Item.Id.Should().Be(older.Id, "it sorts by when it came back");
        later.Visible.First().BackFromSnooze.Should().BeTrue();
        later.UnreadCount.Should().Be(2);
        later.NextWake.Should().BeNull();
    }

    [Fact]
    public void NewActivity_ShouldBreakASnooze()
    {
        var thread = Item("chat", Guid.NewGuid(), Now.AddMinutes(-1));
        var states = new[] { State(thread, snoozedAt: Now.AddHours(-1), until: Now.AddDays(1)) };

        var view = NotificationInboxRules.Apply([thread], states, Now);
        view.Visible.Should().ContainSingle().Which.BackFromSnooze.Should().BeFalse();
        view.Snoozed.Should().BeEmpty();
    }

    [Fact]
    public void Badge_ShouldCountOnlyUnreadShowingItems_AndTheListStopsAtFifty()
    {
        var items = Enumerable.Range(0, 60).Select(i => Item("docker", Guid.NewGuid(), Now.AddMinutes(-i), read: i % 2 == 0)).ToList();
        var view = NotificationInboxRules.Apply(items, [], Now);
        view.Visible.Should().HaveCount(NotificationInboxRules.MaxVisible);
        view.UnreadCount.Should().Be(30, "the badge counts every unread item, not just the first fifty");
    }

    [Theory]
    [InlineData(SnoozeChoice.OneHour, "2026-10-26T13:00:00+00:00")]
    [InlineData(SnoozeChoice.TomorrowMorning, "2026-10-27T09:00:00+00:00")]
    [InlineData(SnoozeChoice.NextWeek, "2026-11-02T09:00:00+00:00")]
    public void SnoozeUntil_ShouldPickTheTime(SnoozeChoice choice, string expected) =>
        NotificationInboxRules.SnoozeUntil(choice, Now, TimeZoneInfo.Utc).Should().Be(DateTimeOffset.Parse(expected));

    [Fact]
    public void NextWeek_OnAMonday_ShouldMeanTheFollowingMonday() =>
        NotificationInboxRules.SnoozeUntil(SnoozeChoice.NextWeek, new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc)
            .Should().Be(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Service_ShouldRecordDoneAndSnooze_PerPerson_AndUndoThem()
    {
        var id = Guid.NewGuid();
        await _service.MarkDoneAsync("grant", "docker", id);
        (await _service.ListAsync("grant")).Should().ContainSingle().Which.DoneAtUnix.Should().Be(Now.ToUnixTimeMilliseconds());
        (await _service.ListAsync("ana")).Should().BeEmpty("states are per person");

        await _service.UndoDoneAsync("grant", "docker", id);
        (await _service.ListAsync("grant")).Single().DoneAtUnix.Should().BeNull();

        await _service.SnoozeAsync("grant", "docker", id, Now.AddHours(1));
        var snoozed = (await _service.ListAsync("grant")).Single();
        snoozed.SnoozedUntilUnix.Should().Be(Now.AddHours(1).ToUnixTimeMilliseconds());
        snoozed.SnoozedAtUnix.Should().Be(Now.ToUnixTimeMilliseconds());

        await _service.UnsnoozeAsync("grant", "docker", id);
        (await _service.ListAsync("grant")).Single().SnoozedUntilUnix.Should().BeNull();

        await _service.Invoking(s => s.SnoozeAsync("grant", "docker", id, Now.AddMinutes(-1))).Should().ThrowAsync<ArgumentException>();
        await _service.Invoking(s => s.MarkDoneAsync("unknown", "docker", id)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Service_ShouldPruneStatesOlderThanNinetyDays()
    {
        var old = Guid.NewGuid();
        await _service.MarkDoneAsync("grant", "docker", old);
        _clock.Set(Now.AddDays(91));
        await _service.MarkDoneAsync("grant", "docker", Guid.NewGuid());
        (await _service.ListAsync("grant")).Select(s => s.ItemId).Should().NotContain(old);
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Set(DateTimeOffset value) => _now = value;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
