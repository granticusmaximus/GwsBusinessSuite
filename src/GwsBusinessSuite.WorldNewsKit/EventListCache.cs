using System.Collections.Concurrent;

namespace GwsBusinessSuite.WorldNewsKit;

// The feed's event lists by time range, built once and shared by every page that shows them. The
// overview query aggregates every report in range, and each page load ran it twice (prerender,
// then the live render) on the droplet's small CPU beside Ollama. A list is rebuilt after Clear
// (a read changed the data) or once it's older than maxAge, so the range's cutoff keeps moving.
public sealed class EventListCache(
    Func<DateTimeOffset, IReadOnlyList<EventOverview>> load, TimeProvider time, TimeSpan maxAge,
    Action<Action>? background = null, Action<Exception>? onError = null)
    : RangeListCache<EventOverview>(load, time, maxAge, background, onError);

// Situations are listed the same way.
//
// A page never waits for a rebuild (30-10-26): one took 50-70 s on the droplet, and the visit after
// every 15-minute read sat on it, with no sign the click had landed. An out-of-date list is handed
// back at once and rebuilt in the background, one rebuild per range at a time, and Clear rebuilds
// the lists held so the next visit finds them fresh. Only a range's first visit waits, and visits
// arriving together share that one build.
public class RangeListCache<T>(
    Func<DateTimeOffset, IReadOnlyList<T>> load, TimeProvider time, TimeSpan maxAge,
    Action<Action>? background = null, Action<Exception>? onError = null)
{
    private readonly ConcurrentDictionary<int, (int Generation, DateTimeOffset BuiltAt, IReadOnlyList<T> Events)> _lists = new();
    private readonly ConcurrentDictionary<int, object> _firstBuilds = new();
    private readonly ConcurrentDictionary<int, byte> _rebuilding = new();
    private readonly Action<Action> _background = background ?? (work => _ = Task.Run(work));
    private int _generation;

    public IReadOnlyList<T> Get(int rangeHours)
    {
        if (_lists.TryGetValue(rangeHours, out var cached))
        {
            if (IsStale(cached)) Rebuild(rangeHours);
            return cached.Events;
        }

        lock (_firstBuilds.GetOrAdd(rangeHours, _ => new object()))
        {
            return _lists.TryGetValue(rangeHours, out cached) ? cached.Events : Build(rangeHours);
        }
    }

    // rebuild: false only marks the lists out of date, for frequent small changes (a summary from
    // the Mac): the next visit still gets the list at once and starts the rebuild.
    public void Clear(bool rebuild = true)
    {
        Interlocked.Increment(ref _generation);
        if (!rebuild) return;
        foreach (var rangeHours in _lists.Keys) Rebuild(rangeHours);
    }

    private bool IsStale((int Generation, DateTimeOffset BuiltAt, IReadOnlyList<T> Events) list) =>
        list.Generation != Volatile.Read(ref _generation) || time.GetUtcNow() - list.BuiltAt >= maxAge;

    // Stamped with the generation read before loading: a Clear during the load leaves this list
    // stale-marked, so it's rebuilt again.
    private IReadOnlyList<T> Build(int rangeHours)
    {
        var now = time.GetUtcNow();
        var generation = Volatile.Read(ref _generation);
        var events = load(now.AddHours(-rangeHours));
        _lists[rangeHours] = (generation, now, events);
        return events;
    }

    private void Rebuild(int rangeHours)
    {
        if (!_rebuilding.TryAdd(rangeHours, 0)) return;
        _background(() =>
        {
            try
            {
                Build(rangeHours);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
            finally
            {
                _rebuilding.TryRemove(rangeHours, out _);
            }
        });
    }
}
