using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.CmsBuilder;

public interface IPageRevisionService
{
    Task<CmsPageRevision> CreateRevisionAsync(
        CmsPage currentPage,
        string label = "",
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CmsPageRevision>> ListAsync(Guid pageId, CancellationToken cancellationToken = default);

    Task<CmsPageRevision?> GetAsync(Guid revisionId, CancellationToken cancellationToken = default);

    Task<CmsPage> RestoreAsync(Guid pageId, Guid revisionId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid revisionId, CancellationToken cancellationToken = default);

    Task DeleteAllForPageAsync(Guid pageId, CancellationToken cancellationToken = default);
}
