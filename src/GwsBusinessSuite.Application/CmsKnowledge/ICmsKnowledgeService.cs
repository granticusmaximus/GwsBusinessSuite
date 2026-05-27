namespace GwsBusinessSuite.Application.CmsKnowledge;

public interface ICmsKnowledgeService
{
    Task<IReadOnlyList<CmsKnowledgeSource>> ListSourcesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CmsKnowledgeQueryResult>> SearchAsync(string query, int take = 5, CancellationToken cancellationToken = default);
}
