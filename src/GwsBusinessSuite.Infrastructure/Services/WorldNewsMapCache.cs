using System.Collections.Concurrent;

namespace GwsBusinessSuite.Infrastructure.Services;

// What Overwatch's World News layer reads on every globe move, kept in memory for everyone on the
// server: the ranked situations, where each one goes, and each one's summary and sources. Before
// this, every pan ran those queries (and placed 300 headlines) while the page's connection waited.
// Cleared after each World News read and each correction, so the globe never shows staler news than
// the World News page; otherwise an entry lives two minutes.
public sealed class WorldNewsMapCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Entry(DateTimeOffset At, Lazy<object?> Value);

    // The value for key, loaded once even when several pans ask at the same moment.
    public T GetOrAdd<T>(string key, Func<T> load)
    {
        var now = time.GetUtcNow();
        while (true)
        {
            var entry = _entries.GetOrAdd(key, _ => new Entry(now, new Lazy<object?>(() => load(), LazyThreadSafetyMode.ExecutionAndPublication)));
            if (now - entry.At <= Lifetime) return (T)entry.Value.Value!;
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }
    }

    public void Clear() => _entries.Clear();
}
