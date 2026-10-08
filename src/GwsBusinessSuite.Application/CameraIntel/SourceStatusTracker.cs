using System.Collections.Concurrent;

namespace GwsBusinessSuite.Application.CameraIntel;

public sealed record SourceStatus(
    string SourceName,
    string Kind,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastError,
    // Most items this source returned for any single view since the app started - a world view
    // returns everything, so this approaches the source's real total once someone zooms out.
    int MaxItemsSeen,
    // Last time the source returned at least one item. Most providers swallow their own HTTP
    // errors and return an empty list, so "answered" alone doesn't prove the source works.
    DateTimeOffset? LastNonEmptyAt = null)
{
    // Threw on its latest attempt (rare - most providers report failure as an empty answer).
    public bool LastAttemptFailed => LastFailureAt is { } failed && (LastSuccessAt is not { } ok || failed > ok);
}

// Per-source "is it working" record for the Overwatch coverage panel. Fed by
// CameraDirectoryService / TrafficIncidentDirectoryService on every view query, so it reflects
// real use rather than a separate probe. In memory: a restart starts it over.
public sealed class SourceStatusTracker(TimeProvider timeProvider)
{
    public const string CameraKind = "cameras";
    public const string IncidentKind = "incidents";

    private readonly ConcurrentDictionary<(string Source, string Kind), SourceStatus> _statuses = new();

    public void RecordSuccess(string sourceName, string kind, int itemCount)
    {
        var now = timeProvider.GetUtcNow();
        _statuses.AddOrUpdate((sourceName, kind),
            _ => new SourceStatus(sourceName, kind, now, null, null, itemCount, itemCount > 0 ? now : null),
            (_, existing) => existing with
            {
                LastSuccessAt = now,
                MaxItemsSeen = Math.Max(existing.MaxItemsSeen, itemCount),
                LastNonEmptyAt = itemCount > 0 ? now : existing.LastNonEmptyAt
            });
    }

    public void RecordFailure(string sourceName, string kind, string error)
    {
        var now = timeProvider.GetUtcNow();
        _statuses.AddOrUpdate((sourceName, kind),
            _ => new SourceStatus(sourceName, kind, null, now, error, 0),
            (_, existing) => existing with { LastFailureAt = now, LastError = error });
    }

    public IReadOnlyList<SourceStatus> Snapshot() =>
        _statuses.Values.OrderBy(s => s.Kind).ThenBy(s => s.SourceName, StringComparer.OrdinalIgnoreCase).ToList();

    public SourceStatus? Get(string sourceName, string kind) =>
        _statuses.TryGetValue((sourceName, kind), out var status) ? status : null;
}
