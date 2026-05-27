using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.CmsBuilder;

public interface ICmsBuilderService
{
    Task<IReadOnlyList<CmsSite>> ListSitesAsync(CancellationToken cancellationToken = default);
    Task<CmsSite?> GetSiteAsync(Guid siteId, CancellationToken cancellationToken = default);
    Task<CmsSite> SaveSiteAsync(CmsSiteEditorModel editor, CancellationToken cancellationToken = default);
    Task DeleteSiteAsync(Guid siteId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CmsPage>> ListPagesAsync(Guid? siteId = null, CancellationToken cancellationToken = default);
    Task<CmsPage?> GetPageAsync(Guid pageId, CancellationToken cancellationToken = default);
    Task<CmsPage> SavePageAsync(CmsPageEditorModel editor, CancellationToken cancellationToken = default);
    Task DeletePageAsync(Guid pageId, CancellationToken cancellationToken = default);
}
