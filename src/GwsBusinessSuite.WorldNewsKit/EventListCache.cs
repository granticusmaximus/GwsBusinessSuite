using System.Collections.Concurrent;

namespace GwsBusinessSuite.WorldNewsKit;

// The feed's event lists by time range, built once and shared by every page that shows them. The
// overview query aggregates every report in range, and each page load ran it twice (prerender,
// then the live render) on the droplet's small CPU beside Ollama. A list is rebuilt after Clear
// (a read changed the data) or once it's older than maxAge, so the range's cutoff keeps moving.
public sealed class EventListCache(Func<DateTimeOffset, IReadOnlyList<EventOverview>> load, TimeProvider time, TimeSpan maxAge)
    : RangeListCache<EventOverview>(load, time, maxAge);

// Situations are listed the same way.
public class RangeListCache<T>(Func<DateTimeOffset, IReadOnlyList<T>> load, TimeProvider time, TimeSpan maxAge)
{
    private readonly ConcurrentDictionary<int, (int Generation, DateTimeOffset BuiltAt, IReadOnlyList<T> Events)> _lists = new();
    private int _generation;

    public IReadOnlyList<T> Get(int rangeHours)
    {
        var now = time.GetUtcNow();
        var generation = Volatile.Read(ref _generation);
        if (_lists.TryGetValue(rangeHours, out var cached) && cached.Generation == generation && now - cached.BuiltAt < maxAge)
            return cached.Events;
        // Stamped with the generation read before loading: a Clear during the load leaves this
        // list stale-marked, so the next Get rebuilds it.
        var events = load(now.AddHours(-rangeHours));
        _lists[rangeHours] = (generation, now, events);
        return events;
    }

    public void Clear() => Interlocked.Increment(ref _generation);
}
