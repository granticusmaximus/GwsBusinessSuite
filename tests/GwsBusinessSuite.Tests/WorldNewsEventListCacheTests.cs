using FluentAssertions;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// World News event lists from memory (27-10-26): page loads stopped re-running the overview query.
public sealed class WorldNewsEventListCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static EventOverview Event(string id) => new(id, id, 1, 1, [], [], Start, Start);

    [Fact]
    public void Get_ShouldLoadOncePerRange_UntilClearedOrOld()
    {
        var clock = new Clock();
        var loads = new List<DateTimeOffset>();
        var cache = new EventListCache(since => { loads.Add(since); return [Event($"e{loads.Count}")]; }, clock, TimeSpan.FromMinutes(15));

        cache.Get(24).Should().ContainSingle(e => e.Id == "e1");
        cache.Get(24).Should().ContainSingle(e => e.Id == "e1", "the second visit reads memory");
        cache.Get(168).Should().ContainSingle(e => e.Id == "e2", "each range is its own list");
        loads.Should().Equal(Start.AddHours(-24), Start.AddHours(-168));

        cache.Clear();
        cache.Get(24).Should().ContainSingle(e => e.Id == "e3", "a read changed the data");

        clock.Now = Start.AddMinutes(16);
        cache.Get(24).Should().ContainSingle(e => e.Id == "e4", "past maxAge the cutoff has moved on");
        loads.Last().Should().Be(Start.AddMinutes(16).AddHours(-24));
    }

    [Fact]
    public void AClearDuringALoad_ShouldNotLeaveTheOldListLookingFresh()
    {
        var clock = new Clock();
        EventListCache cache = null!;
        var loads = 0;
        cache = new EventListCache(_ =>
        {
            loads++;
            if (loads == 1) cache.Clear(); // a read finishes while the first list is being built
            return [Event($"e{loads}")];
        }, clock, TimeSpan.FromMinutes(15));

        cache.Get(24).Should().ContainSingle(e => e.Id == "e1");
        cache.Get(24).Should().ContainSingle(e => e.Id == "e2", "the list built before the read is rebuilt");
    }
}
