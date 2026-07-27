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

public interface ISentinelPresenceService
{
    Task EnterAsync(Guid sessionId, string username, Guid wikiPageId, CancellationToken cancellationToken = default);
    Task TouchAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task LeaveAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelPresenceView>> ListAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
}

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

public sealed record SentinelCursorPosition(
    string Username,
    Guid BlockId,
    DateTimeOffset UpdatedAt,
    int? Start = null,
    int? End = null);

/// <summary>
/// Process-local remote cursor tracking, including character selection offsets within the
/// active block. Content reconciliation remains block-identity three-way merge; these offsets
/// are visual presence metadata and never mutate document content.
/// A cursor position is extremely perishable and purely a visual nicety, so unlike
/// SentinelPresenceLease this is in-memory only (no DB backing, no cross-instance polling
/// fallback) - on a multi-instance deployment a remote cursor simply won't be visible to a
/// circuit on a different instance, an acceptable degradation matching the same reasoning
/// SentinelCollaborationNotifier documents for itself.
/// </summary>
public sealed class SentinelCursorTracker(TimeProvider timeProvider)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Dictionary<string, SentinelCursorPosition>> _cursorsByPage = new();

    public event Action<Guid>? Moved;

    public void Move(Guid wikiPageId, string username, Guid blockId, int? start = null, int? end = null)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        lock (_gate)
        {
            if (!_cursorsByPage.TryGetValue(wikiPageId, out var byUser))
            {
                byUser = new Dictionary<string, SentinelCursorPosition>(StringComparer.OrdinalIgnoreCase);
                _cursorsByPage[wikiPageId] = byUser;
            }
            int? normalizedStart = start is null ? null : Math.Max(0, start.Value);
            var normalizedEnd = end is null ? normalizedStart : Math.Max(normalizedStart ?? 0, end.Value);
            byUser[username] = new SentinelCursorPosition(
                username,
                blockId,
                timeProvider.GetUtcNow(),
                normalizedStart,
                normalizedEnd);
        }
        Moved?.Invoke(wikiPageId);
    }

    public void Leave(Guid wikiPageId, string username)
    {
        bool removed;
        lock (_gate)
        {
            removed = _cursorsByPage.TryGetValue(wikiPageId, out var byUser) && byUser.Remove(username);
        }
        if (removed) Moved?.Invoke(wikiPageId);
    }

    public IReadOnlyList<SentinelCursorPosition> List(Guid wikiPageId, string excludingUsername)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!_cursorsByPage.TryGetValue(wikiPageId, out var byUser)) return [];
            var cutoff = now - Ttl;
            return byUser.Values
                .Where(cursor => cursor.UpdatedAt >= cutoff
                    && !string.Equals(cursor.Username, excludingUsername, StringComparison.OrdinalIgnoreCase))
                .OrderBy(cursor => cursor.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
