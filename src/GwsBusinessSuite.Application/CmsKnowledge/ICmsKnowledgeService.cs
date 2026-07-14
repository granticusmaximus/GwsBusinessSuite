using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.CmsKnowledge;

public interface ICmsKnowledgeService
{
    Task<IReadOnlyList<CmsKnowledgeSource>> ListSourcesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CmsKnowledgeEntry>> ListEntriesAsync(Guid? sourceId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CmsKnowledgeQueryResult>> SearchAsync(string query, int take = 5, CancellationToken cancellationToken = default);

    Task<CmsKnowledgeSource> SaveSourceAsync(CmsKnowledgeSourceEditorModel editor, CancellationToken cancellationToken = default);

    // Cascades to delete every entry under this source.
    Task DeleteSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);

    Task<CmsKnowledgeEntry> SaveEntryAsync(CmsKnowledgeEntryEditorModel editor, CancellationToken cancellationToken = default);

    Task DeleteEntryAsync(Guid entryId, CancellationToken cancellationToken = default);
}
