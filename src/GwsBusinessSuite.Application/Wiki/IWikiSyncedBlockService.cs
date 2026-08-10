namespace GwsBusinessSuite.Application.Wiki;

// Backs the "synced_block" WikiBlock type: every instance of a synced block across the
// workspace carries the same Props["sourceId"] and points at one row managed here. See
// WikiSyncedBlockSource (Domain) and WikiService.GetPageAsync/SavePageAsync (hydrate on
// read, propagate on write) for how instances stay in sync without ever forking content.
public interface IWikiSyncedBlockService
{
    Task<Guid> CreateAsync(
        IReadOnlyList<WikiRichTextSpan> initialRichText,
        Guid? originWikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task UpdateContentAsync(
        Guid sourceId,
        IReadOnlyList<WikiRichTextSpan> richText,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<WikiRichTextSpan>>> GetContentBatchAsync(
        IReadOnlyCollection<Guid> sourceIds,
        CancellationToken cancellationToken = default);
}
