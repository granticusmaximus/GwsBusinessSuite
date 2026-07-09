using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

public interface IWikiService
{
    Task<IReadOnlyList<WikiPage>> ListPagesAsync(CancellationToken cancellationToken = default);
    Task<WikiPage?> GetPageAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
    Task<WikiPage> SavePageAsync(WikiPageEditorModel editor, string performedBy, CancellationToken cancellationToken = default);
    Task DeletePageAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default);

    // Git-backed history - unbounded, sourced live from the repo rather than a DB table.
    Task<IReadOnlyList<WikiRevisionView>> GetHistoryAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
    Task<string?> GetDiffAsync(Guid wikiPageId, string fromSha, string toSha, CancellationToken cancellationToken = default);
    Task<string?> GetRevisionContentAsync(Guid wikiPageId, string sha, CancellationToken cancellationToken = default);
    Task<WikiPage> RevertToRevisionAsync(Guid wikiPageId, string sha, string performedBy, CancellationToken cancellationToken = default);
}
