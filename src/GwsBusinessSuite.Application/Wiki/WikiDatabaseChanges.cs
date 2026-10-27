namespace GwsBusinessSuite.Application.Wiki;

// RowIds are the rows saved (added, edited, trashed or deleted; not ones only re-sorted) and
// ParentRowIds their parents, for sub-items; both are empty when only the database's properties,
// board settings or row order changed.
// Origin is the DbContext that saved, so a circuit can tell its own writes from everyone else's.
public sealed record WikiDatabaseChanged(
    Guid WikiDatabaseId,
    IReadOnlyCollection<Guid> RowIds,
    IReadOnlyCollection<Guid> ParentRowIds,
    string Actor,
    object? Origin,
    DateTimeOffset OccurredAt);

/// <summary>
/// Boards K6, live updates: every save that touches a Sentinel database's rows, properties or
/// board settings, from any screen, workflow or background job (published by
/// WikiDatabaseChangeInterceptor after the save succeeds). Process-local, like
/// SentinelCollaborationNotifier; a second web instance would need a backplane.
/// </summary>
public sealed class WikiDatabaseChangeNotifier(TimeProvider timeProvider)
{
    public event Action<WikiDatabaseChanged>? Changed;

    public void Publish(Guid wikiDatabaseId, IReadOnlyCollection<Guid> rowIds, IReadOnlyCollection<Guid> parentRowIds, string actor, object? origin)
    {
        var change = new WikiDatabaseChanged(wikiDatabaseId, rowIds, parentRowIds, actor, origin, timeProvider.GetUtcNow());
        if (Changed is not { } handlers) return;
        // One listener's failure (a disposed circuit) mustn't stop the others, or the save's caller.
        foreach (var handler in handlers.GetInvocationList().Cast<Action<WikiDatabaseChanged>>())
        {
            try { handler(change); }
            catch (Exception) { }
        }
    }
}
