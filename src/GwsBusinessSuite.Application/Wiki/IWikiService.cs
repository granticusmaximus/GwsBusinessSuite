using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

public interface IWikiService
{
    Task<IReadOnlyList<WikiPage>> ListPagesAsync(CancellationToken cancellationToken = default);
    Task<WikiPage?> GetPageAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
    Task<WikiPage> SavePageAsync(WikiPageEditorModel editor, string performedBy, CancellationToken cancellationToken = default);
    Task DeletePageAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default);
    Task ReorderPageAsync(Guid wikiPageId, Guid? newParentWikiPageId, int newSortOrder, string performedBy, CancellationToken cancellationToken = default);

    // Bounded DB-snapshot history (WikiPageRevision), replacing the old git-log-based history.
    Task<IReadOnlyList<WikiRevisionView>> GetHistoryAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
    Task<string?> GetStructuralDiffAsync(Guid wikiPageId, Guid fromRevisionId, Guid toRevisionId, CancellationToken cancellationToken = default);
    Task<WikiPage> RevertToRevisionAsync(Guid wikiPageId, Guid revisionId, string performedBy, CancellationToken cancellationToken = default);
}
