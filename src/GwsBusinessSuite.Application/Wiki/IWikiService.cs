using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

public interface IWikiService
{
    Task<IReadOnlyList<WikiPage>> ListPagesAsync(bool includeTrashed = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WikiPage>> ListTrashedPagesAsync(CancellationToken cancellationToken = default);
    Task<WikiPage?> GetPageAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
    // createRevisionCheckpoint=false backs silent background autosave (Wiki.razor) - every
    // other field/permission/concurrency rule still applies, it just skips minting a
    // WikiPageRevision so a burst of debounced autosave ticks doesn't spam version history
    // the way an explicit "Save changes" click deliberately does.
    Task<WikiPage> SavePageAsync(
        WikiPageEditorModel editor, string performedBy, bool createRevisionCheckpoint = true, CancellationToken cancellationToken = default);
    Task<WikiPage> DuplicatePageAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default);

    // Soft-delete: also trashes every descendant page, and every database parented anywhere
    // in that subtree, so a trashed branch of the tree disappears together. Reversible via
    // RestorePageAsync/RestoreDatabaseAsync (WikiDatabaseService) on each item individually -
    // restoring the parent does not automatically restore its (also-trashed) descendants.
    Task TrashPageAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default);

    // Restores just this one page. If its original parent is itself still trashed (or gone),
    // the page is reparented to the workspace root instead of coming back invisible.
    Task RestorePageAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default);

    Task DeletePagePermanentlyAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default);
    Task ReorderPageAsync(Guid wikiPageId, Guid? newParentWikiPageId, int newSortOrder, string performedBy, CancellationToken cancellationToken = default);

    // Bounded DB-snapshot history (WikiPageRevision), replacing the old git-log-based history.
    Task<IReadOnlyList<WikiRevisionView>> GetHistoryAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
    Task<string?> GetStructuralDiffAsync(Guid wikiPageId, Guid fromRevisionId, Guid toRevisionId, CancellationToken cancellationToken = default);
    Task<WikiPage> RevertToRevisionAsync(Guid wikiPageId, Guid revisionId, string performedBy, CancellationToken cancellationToken = default);
}
