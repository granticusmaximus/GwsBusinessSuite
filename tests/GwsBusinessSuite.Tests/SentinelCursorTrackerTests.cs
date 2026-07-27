using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelCursorTrackerTests
{
    [Fact]
    public void Move_ShouldExcludeTheRequestingUsersOwnCursor()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var tracker = new SentinelCursorTracker(time);
        var pageId = Guid.NewGuid();
        var blockA = Guid.NewGuid();
        var blockB = Guid.NewGuid();

        tracker.Move(pageId, "grant", blockA);
        tracker.Move(pageId, "morgan", blockB);

        var forGrant = tracker.List(pageId, "grant");
        forGrant.Should().ContainSingle(cursor => cursor.Username == "morgan" && cursor.BlockId == blockB);

        var forMorgan = tracker.List(pageId, "morgan");
        forMorgan.Should().ContainSingle(cursor => cursor.Username == "grant" && cursor.BlockId == blockA);
    }

    [Fact]
    public void Move_ShouldOverwriteTheSameUsersPreviousBlockOnThatPage()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var tracker = new SentinelCursorTracker(time);
        var pageId = Guid.NewGuid();
        var firstBlock = Guid.NewGuid();
        var secondBlock = Guid.NewGuid();

        tracker.Move(pageId, "morgan", firstBlock);
        tracker.Move(pageId, "morgan", secondBlock);

        var cursors = tracker.List(pageId, "someone-else");
        cursors.Should().ContainSingle(cursor => cursor.BlockId == secondBlock);
    }

    [Fact]
    public void Move_ShouldRetainNormalizedCharacterSelectionOffsets()
    {
        var tracker = new SentinelCursorTracker(TimeProvider.System);
        var pageId = Guid.NewGuid();
        var blockId = Guid.NewGuid();

        tracker.Move(pageId, "morgan", blockId, 12, 5);

        tracker.List(pageId, "grant").Should().ContainSingle(cursor =>
            cursor.Start == 12 && cursor.End == 12);
    }

    [Fact]
    public void List_ShouldDropCursorsOlderThanTheTtl()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var tracker = new SentinelCursorTracker(time);
        var pageId = Guid.NewGuid();

        tracker.Move(pageId, "morgan", Guid.NewGuid());
        time.Advance(TimeSpan.FromSeconds(11));

        tracker.List(pageId, "grant").Should().BeEmpty("a cursor position older than the TTL is stale");
    }

    [Fact]
    public void Leave_ShouldRemoveTheUsersCursorAndRaiseMovedOnlyWhenSomethingChanged()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var tracker = new SentinelCursorTracker(time);
        var pageId = Guid.NewGuid();
        var changedPages = new List<Guid>();
        tracker.Moved += changedPages.Add;

        tracker.Move(pageId, "morgan", Guid.NewGuid());
        tracker.Leave(pageId, "morgan");

        tracker.List(pageId, "grant").Should().BeEmpty();
        changedPages.Should().HaveCount(2, "one raise for Move, one for the Leave that actually removed a cursor");

        changedPages.Clear();
        tracker.Leave(pageId, "morgan");
        changedPages.Should().BeEmpty("leaving a user who already left has nothing to change");
    }

    [Fact]
    public void List_ShouldScopeCursorsToTheirOwnPage()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var tracker = new SentinelCursorTracker(time);
        var firstPage = Guid.NewGuid();
        var secondPage = Guid.NewGuid();

        tracker.Move(firstPage, "morgan", Guid.NewGuid());

        tracker.List(secondPage, "grant").Should().BeEmpty();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan by) => _utcNow += by;
    }
}
