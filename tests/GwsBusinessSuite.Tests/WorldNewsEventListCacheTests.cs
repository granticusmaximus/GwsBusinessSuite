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

    // Background work queued, then run when the test says, so "handed back at once" is checkable.
    private sealed class Later
    {
        private readonly Queue<Action> _work = new();
        public void Queue(Action work) => _work.Enqueue(work);
        public int Waiting => _work.Count;
        public void RunAll() { while (_work.Count > 0) _work.Dequeue()(); }
    }

    [Fact]
    public void Get_ShouldLoadOncePerRange_AndHandBackAnOldListAtOnce_WhileItsRebuilt()
    {
        var clock = new Clock();
        var later = new Later();
        var loads = new List<DateTimeOffset>();
        var cache = new EventListCache(since => { loads.Add(since); return [Event($"e{loads.Count}")]; }, clock, TimeSpan.FromMinutes(15), later.Queue);

        cache.Get(24).Should().ContainSingle(e => e.Id == "e1", "the first visit for a range waits for it");
        cache.Get(24).Should().ContainSingle(e => e.Id == "e1", "the second visit reads memory");
        cache.Get(168).Should().ContainSingle(e => e.Id == "e2", "each range is its own list");
        loads.Should().Equal(Start.AddHours(-24), Start.AddHours(-168));
        later.Waiting.Should().Be(0);

        clock.Now = Start.AddMinutes(16);
        cache.Get(24).Should().ContainSingle(e => e.Id == "e1", "an old list comes back at once, not after a rebuild");
        cache.Get(24).Should().ContainSingle(e => e.Id == "e1");
        later.Waiting.Should().Be(1, "one rebuild per range, however many visits ask");
        later.RunAll();
        cache.Get(24).Should().ContainSingle(e => e.Id == "e3");
        loads.Last().Should().Be(Start.AddMinutes(16).AddHours(-24), "the rebuild moves the cutoff on");
    }

    [Fact]
    public void Clear_ShouldRebuildTheListsHeld_OrJustMarkThemOld()
    {
        var clock = new Clock();
        var later = new Later();
        var loads = 0;
        var cache = new EventListCache(_ => [Event($"e{++loads}")], clock, TimeSpan.FromMinutes(15), later.Queue);
        cache.Get(24);
        cache.Get(168);

        cache.Clear();
        later.Waiting.Should().Be(2, "a read rebuilds every list held, before anyone asks");
        later.RunAll();
        loads.Should().Be(4);
        var rebuilt = cache.Get(24);
        rebuilt.Should().NotContain(e => e.Id == "e1");
        later.Waiting.Should().Be(0, "it's fresh again");

        cache.Clear(rebuild: false);
        later.Waiting.Should().Be(0, "a small change only marks the lists old");
        cache.Get(24).Should().BeSameAs(rebuilt);
        later.Waiting.Should().Be(1, "the next visit starts the rebuild");
    }

    [Fact]
    public void AClearDuringALoad_ShouldNotLeaveTheOldListLookingFresh()
    {
        var clock = new Clock();
        var later = new Later();
        EventListCache cache = null!;
        var loads = 0;
        cache = new EventListCache(_ =>
        {
            loads++;
            if (loads == 1) cache.Clear(); // a read finishes while the first list is being built
            return [Event($"e{loads}")];
        }, clock, TimeSpan.FromMinutes(15), later.Queue);

        cache.Get(24).Should().ContainSingle(e => e.Id == "e1");
        cache.Get(24).Should().ContainSingle(e => e.Id == "e1", "handed back at once");
        later.RunAll();
        cache.Get(24).Should().ContainSingle(e => e.Id == "e2", "the list built before the read is rebuilt");
    }

    [Fact]
    public async Task VisitsArrivingTogether_ShouldShareTheFirstBuild_AndAFailedRebuildKeepsTheList()
    {
        var clock = new Clock();
        var loads = 0;
        var failures = new List<Exception>();
        using var gate = new ManualResetEventSlim();
        var cache = new EventListCache(_ =>
        {
            if (Interlocked.Increment(ref loads) == 1) gate.Wait(TimeSpan.FromSeconds(5));
            else throw new InvalidOperationException("database busy");
            return [Event("e1")];
        }, clock, TimeSpan.FromMinutes(15), work => work(), failures.Add);

        var first = Task.Run(() => cache.Get(24));
        var second = Task.Run(() => cache.Get(24));
        await Task.Delay(100);
        gate.Set();
        (await first).Should().ContainSingle(e => e.Id == "e1");
        (await second).Should().ContainSingle(e => e.Id == "e1");
        loads.Should().Be(1, "the second visit waited for the first build instead of starting its own");

        cache.Clear();
        failures.Should().ContainSingle().Which.Message.Should().Be("database busy");
        cache.Get(24).Should().ContainSingle(e => e.Id == "e1", "a failed rebuild leaves the list it had");
    }
}
