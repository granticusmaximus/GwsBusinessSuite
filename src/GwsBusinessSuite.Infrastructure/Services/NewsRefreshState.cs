using GwsBusinessSuite.Application.NewsIntelligence;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NewsRefreshState
{
    private readonly object _gate = new();
    private readonly HashSet<string> _activeItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NewsRefreshTiming> _timings = [];
    private NewsRefreshStatus _snapshot = NewsRefreshStatus.Idle;

    public NewsRefreshStatus Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public void Begin(int totalItems)
    {
        lock (_gate)
        {
            _activeItems.Clear();
            _timings.Clear();
            _snapshot = new NewsRefreshStatus(
                true, "Preparing", 0, totalItems, DateTimeOffset.UtcNow, null, [], [], null);
        }
    }

    public void StartItem(string name)
    {
        lock (_gate)
        {
            _activeItems.Add(name);
            Publish("Fetching and summarizing", null);
        }
    }

    public void CompleteItem(string name, IEnumerable<NewsRefreshTiming> timings)
    {
        lock (_gate)
        {
            _activeItems.Remove(name);
            _timings.AddRange(timings);
            _snapshot = _snapshot with { CompletedItems = _snapshot.CompletedItems + 1 };
            Publish(_activeItems.Count == 0 ? "Finalizing" : "Fetching and summarizing", null);
        }
    }

    public void FailItem(string name, Exception exception, IEnumerable<NewsRefreshTiming> timings)
    {
        lock (_gate)
        {
            _activeItems.Remove(name);
            _timings.AddRange(timings);
            _snapshot = _snapshot with { CompletedItems = _snapshot.CompletedItems + 1 };
            Publish(_activeItems.Count == 0 ? "Finalizing" : "Fetching and summarizing", exception.Message);
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            _activeItems.Clear();
            _snapshot = _snapshot with
            {
                IsRunning = false,
                Phase = string.IsNullOrWhiteSpace(_snapshot.LastError) ? "Complete" : "Complete with errors",
                CompletedAt = DateTimeOffset.UtcNow,
                ActiveItems = [],
                Timings = _timings.ToList()
            };
        }
    }

    private void Publish(string phase, string? error)
    {
        _snapshot = _snapshot with
        {
            Phase = phase,
            ActiveItems = _activeItems.OrderBy(x => x).ToList(),
            Timings = _timings.ToList(),
            LastError = error ?? _snapshot.LastError
        };
    }
}
