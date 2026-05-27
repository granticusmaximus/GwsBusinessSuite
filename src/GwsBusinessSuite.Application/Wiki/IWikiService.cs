using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

public interface IWikiService
{
    Task<IReadOnlyList<WikiPage>> ListPagesAsync(CancellationToken cancellationToken = default);
    Task<WikiPage?> GetPageAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
    Task<WikiPage> SavePageAsync(WikiPageEditorModel editor, CancellationToken cancellationToken = default);
    Task DeletePageAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
}
