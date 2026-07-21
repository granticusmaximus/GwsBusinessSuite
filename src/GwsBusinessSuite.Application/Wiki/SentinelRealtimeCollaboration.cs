namespace GwsBusinessSuite.Application.Wiki;

public sealed record SentinelCollaborationChange(
    Guid WikiPageId,
    string Kind,
    string Actor,
    DateTimeOffset OccurredAt);

/// <summary>
/// Process-local collaboration fan-out. Blazor Server already carries component rerenders over
/// each circuit, so a second browser SignalR connection is unnecessary. Replace this boundary
/// with a distributed backplane when the web app runs on more than one instance.
/// </summary>
public sealed class SentinelCollaborationNotifier(TimeProvider timeProvider)
{
    public event Action<SentinelCollaborationChange>? Changed;

    public void Publish(Guid wikiPageId, string kind, string actor) =>
        Changed?.Invoke(new SentinelCollaborationChange(wikiPageId, kind, actor, timeProvider.GetUtcNow()));
}

public sealed record SentinelPresenceView(
    string Username,
    int SessionCount,
    DateTimeOffset LastSeenAt);

/// <summary>
/// Process-local, heartbeat-expiring page presence. No identity is accepted from browser state;
/// components obtain usernames through the server-side current-user accessor.
/// </summary>
public sealed class SentinelPresenceTracker(TimeProvider timeProvider)
{
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, PresenceSession> _sessions = new();

    public event Action<Guid>? PresenceChanged;

    public void EnterPage(Guid sessionId, string username, Guid wikiPageId)
    {
        var now = timeProvider.GetUtcNow();
        Guid? previousPageId = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var previous))
            {
                previousPageId = previous.WikiPageId;
            }
            _sessions[sessionId] = new PresenceSession(
                NormalizeUsername(username), DisplayUsername(username), wikiPageId, now);
        }

        if (previousPageId is { } oldPageId && oldPageId != wikiPageId) PresenceChanged?.Invoke(oldPageId);
        PresenceChanged?.Invoke(wikiPageId);
    }

    public void Touch(Guid sessionId)
    {
        Guid? pageId = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.LastSeenAt = timeProvider.GetUtcNow();
                pageId = session.WikiPageId;
            }
        }
        if (pageId.HasValue) PresenceChanged?.Invoke(pageId.Value);
    }

    public void Leave(Guid sessionId)
    {
        Guid? pageId = null;
        lock (_gate)
        {
            if (_sessions.Remove(sessionId, out var removed)) pageId = removed.WikiPageId;
        }
        if (pageId.HasValue) PresenceChanged?.Invoke(pageId.Value);
    }

    public IReadOnlyList<SentinelPresenceView> GetPagePresence(Guid wikiPageId)
    {
        var now = timeProvider.GetUtcNow();
        List<Guid> expiredPages;
        List<SentinelPresenceView> result;
        lock (_gate)
        {
            expiredPages = _sessions
                .Where(pair => now - pair.Value.LastSeenAt > SessionTimeout)
                .Select(pair => pair.Value.WikiPageId)
                .Distinct()
                .ToList();
            foreach (var expiredId in _sessions
                         .Where(pair => now - pair.Value.LastSeenAt > SessionTimeout)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _sessions.Remove(expiredId);
            }

            result = _sessions.Values
                .Where(session => session.WikiPageId == wikiPageId)
                .GroupBy(session => session.NormalizedUsername, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SentinelPresenceView(
                    group.OrderByDescending(session => session.LastSeenAt).First().DisplayUsername,
                    group.Count(),
                    group.Max(session => session.LastSeenAt)))
                .OrderBy(presence => presence.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (var expiredPageId in expiredPages) PresenceChanged?.Invoke(expiredPageId);
        return result;
    }

    private static string NormalizeUsername(string username) =>
        string.IsNullOrWhiteSpace(username) ? "unknown" : username.Trim().ToLowerInvariant();

    private static string DisplayUsername(string username) =>
        string.IsNullOrWhiteSpace(username) ? "Unknown" : username.Trim();

    private sealed class PresenceSession(
        string normalizedUsername,
        string displayUsername,
        Guid wikiPageId,
        DateTimeOffset lastSeenAt)
    {
        public string NormalizedUsername { get; } = normalizedUsername;
        public string DisplayUsername { get; } = displayUsername;
        public Guid WikiPageId { get; } = wikiPageId;
        public DateTimeOffset LastSeenAt { get; set; } = lastSeenAt;
    }
}
