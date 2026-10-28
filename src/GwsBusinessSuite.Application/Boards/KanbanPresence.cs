namespace GwsBusinessSuite.Application.Boards;

// Who else has a board open (Boards K7), and which card each has open, for the rings on the
// header's avatars and the pulse on a card someone is in. One session per open board page; it
// checks in every 30 seconds and is forgotten 90 seconds after its last check-in (a closed laptop).
// Process-local, like SentinelPresenceTracker.
public sealed record KanbanViewer(Guid SessionId, string Username, Guid? OpenCardId);

public sealed class KanbanPresence(TimeProvider timeProvider)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private sealed record Session(Guid BoardId, string Username, Guid? OpenCardId, DateTimeOffset LastSeen);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Session> _sessions = new();

    // The board whose viewers changed.
    public event Action<Guid>? Changed;

    // Joining a board, or saying which card is open now (null: none). Tells the board only when
    // something it shows changed.
    public void Enter(Guid sessionId, Guid boardId, string username, Guid? openCardId)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        Guid? left = null;
        var changed = true;
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var was))
            {
                if (was.BoardId != boardId) left = was.BoardId;
                changed = was.BoardId != boardId || was.OpenCardId != openCardId || was.Username != username;
            }
            _sessions[sessionId] = new Session(boardId, username.Trim(), openCardId, timeProvider.GetUtcNow());
        }
        if (left is { } previous) Notify(previous);
        if (changed) Notify(boardId);
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
        Guid? board = null;
        lock (_gate)
        {
            if (_sessions.Remove(sessionId, out var session)) board = session.BoardId;
        }
        if (board is { } id) Notify(id);
    }

    // Everyone else on the board (other people, and the same person in another window), by name.
    public IReadOnlyList<KanbanViewer> Others(Guid boardId, Guid exceptSessionId)
    {
        var cutoff = timeProvider.GetUtcNow() - Timeout;
        lock (_gate)
        {
            foreach (var stale in _sessions.Where(pair => pair.Value.LastSeen < cutoff).Select(pair => pair.Key).ToList())
                _sessions.Remove(stale);
            return _sessions
                .Where(pair => pair.Key != exceptSessionId && pair.Value.BoardId == boardId)
                .Select(pair => new KanbanViewer(pair.Key, pair.Value.Username, pair.Value.OpenCardId))
                .OrderBy(viewer => viewer.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void Notify(Guid boardId)
    {
        if (Changed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<Guid>>())
        {
            try { handler(boardId); }
            catch (Exception) { }
        }
    }
}
