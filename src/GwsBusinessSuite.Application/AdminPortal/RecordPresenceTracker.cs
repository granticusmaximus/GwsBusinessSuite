namespace GwsBusinessSuite.Application.AdminPortal;

public readonly record struct RecordPresenceKey(string Kind, Guid Id)
{
    public static RecordPresenceKey Create(string kind, Guid id) => new(kind.Trim().ToLowerInvariant(), id);
}

// FocusId: the part of the record this viewer has open, when the record has parts (a board's
// open card); null otherwise.
public sealed record RecordViewer(Guid SessionId, string Username, Guid? FocusId = null);

/// <summary>
/// The app's one process-local presence tracker: who else has a record open (contacts, invoices,
/// support tickets, boards via <c>KanbanPresence</c>). Each open record checks in every 30 seconds
/// and disappears after 90 seconds if a circuit vanishes without disposing cleanly. Sentinel pages
/// use persisted leases instead (SentinelPresenceService), because they also lock edits.
/// </summary>
public sealed class RecordPresenceTracker(TimeProvider timeProvider)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private sealed record Session(RecordPresenceKey Record, string Username, Guid? FocusId, DateTimeOffset LastSeen);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Session> _sessions = [];

    public event Action<RecordPresenceKey>? Changed;

    // Opening a record, or saying which part of it is open now. Tells the record only when
    // something it shows changed.
    public void Enter(Guid sessionId, RecordPresenceKey record, string username, Guid? focusId = null)
    {
        if (record.Id == Guid.Empty || string.IsNullOrWhiteSpace(record.Kind) || string.IsNullOrWhiteSpace(username)) return;
        var name = username.Trim();
        RecordPresenceKey? left = null;
        var changed = true;
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var previous))
            {
                if (previous.Record != record) left = previous.Record;
                changed = previous.Record != record || previous.FocusId != focusId
                    || !string.Equals(previous.Username, name, StringComparison.Ordinal);
            }
            _sessions[sessionId] = new Session(record, name, focusId, timeProvider.GetUtcNow());
        }
        if (left is { } previousRecord) Notify(previousRecord);
        if (changed) Notify(record);
    }

    public void Touch(Guid sessionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
                _sessions[sessionId] = session with { LastSeen = timeProvider.GetUtcNow() };
        }
    }

    public void Leave(Guid sessionId)
    {
        RecordPresenceKey? record = null;
        lock (_gate)
        {
            if (_sessions.Remove(sessionId, out var session)) record = session.Record;
        }
        if (record is { } key) Notify(key);
    }

    // Everyone else on the record (other people, and the same person in another window), by name.
    public IReadOnlyList<RecordViewer> Others(RecordPresenceKey record, Guid exceptSessionId)
    {
        var cutoff = timeProvider.GetUtcNow() - Timeout;
        lock (_gate)
        {
            foreach (var stale in _sessions.Where(pair => pair.Value.LastSeen < cutoff).Select(pair => pair.Key).ToList())
                _sessions.Remove(stale);
            return _sessions
                .Where(pair => pair.Key != exceptSessionId && pair.Value.Record == record)
                .Select(pair => new RecordViewer(pair.Key, pair.Value.Username, pair.Value.FocusId))
                .OrderBy(viewer => viewer.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void Notify(RecordPresenceKey record)
    {
        if (Changed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<RecordPresenceKey>>())
        {
            try { handler(record); }
            catch (Exception) { }
        }
    }
}
