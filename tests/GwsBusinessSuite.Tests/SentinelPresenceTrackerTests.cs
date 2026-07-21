using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelPresenceTrackerTests
{
    [Fact]
    public void EnterTouchMoveAndLeave_ShouldMaintainUniquePerUserPagePresence()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var tracker = new SentinelPresenceTracker(time);
        var firstPage = Guid.NewGuid();
        var secondPage = Guid.NewGuid();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        var changedPages = new List<Guid>();
        tracker.PresenceChanged += changedPages.Add;

        tracker.EnterPage(firstSession, "Grant", firstPage);
        tracker.EnterPage(secondSession, "GRANT", firstPage);

        tracker.GetPagePresence(firstPage).Should().ContainSingle(presence =>
            string.Equals(presence.Username, "grant", StringComparison.OrdinalIgnoreCase)
            && presence.SessionCount == 2);

        time.Advance(TimeSpan.FromSeconds(20));
        tracker.Touch(firstSession);
        tracker.EnterPage(secondSession, "Grant", secondPage);

        tracker.GetPagePresence(firstPage).Should().ContainSingle(presence => presence.SessionCount == 1);
        tracker.GetPagePresence(secondPage).Should().ContainSingle();
        changedPages.Should().Contain(firstPage).And.Contain(secondPage);

        tracker.Leave(firstSession);
        tracker.GetPagePresence(firstPage).Should().BeEmpty();
    }

    [Fact]
    public void GetPagePresence_ShouldPruneSessionsWithoutAHeartbeat()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        var tracker = new SentinelPresenceTracker(time);
        var pageId = Guid.NewGuid();
        tracker.EnterPage(Guid.NewGuid(), "Grant", pageId);

        time.Advance(SentinelPresenceTracker.SessionTimeout + TimeSpan.FromSeconds(1));

        tracker.GetPagePresence(pageId).Should().BeEmpty();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan by) => _utcNow += by;
    }
}
