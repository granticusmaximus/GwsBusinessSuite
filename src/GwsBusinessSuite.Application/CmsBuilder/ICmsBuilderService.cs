using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.CmsBuilder;

public interface ICmsBuilderService
{
    Task<IReadOnlyList<CmsSite>> ListSitesAsync(CancellationToken cancellationToken = default);
    Task<CmsSite?> GetSiteAsync(Guid siteId, CancellationToken cancellationToken = default);
    Task<CmsSite?> GetSiteBySlugAsync(string siteSlug, CancellationToken cancellationToken = default);
    Task<CmsSite> SaveSiteAsync(CmsSiteEditorModel editor, CancellationToken cancellationToken = default);
    Task DeleteSiteAsync(Guid siteId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CmsPage>> ListPagesAsync(Guid? siteId = null, bool includeTrashed = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CmsPage>> ListTrashedPagesAsync(Guid siteId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CmsPageCategory>> ListPageCategoriesAsync(Guid siteId, CancellationToken cancellationToken = default);
    Task<CmsPage?> GetPageAsync(Guid pageId, CancellationToken cancellationToken = default);
    Task<CmsPage?> GetPageBySlugAsync(Guid siteId, string pageSlug, CancellationToken cancellationToken = default);
    Task<CmsPage?> GetPageByFullPathAsync(Guid siteId, string fullPath, bool includeUnpublished = false, CancellationToken cancellationToken = default);
    // actor is optional and only affects the automation cms.pagePublishedTrigger loop guard,
    // same actor-tagging convention as WikiDatabaseService.SaveRowAsync - the automation action
    // node cms.savePage passes "automation-engine" (or "automation-engine:chained" when the
    // workflow opts into downstream chaining).
    Task<CmsPage> SavePageAsync(CmsPageEditorModel editor, string? actor = null, CancellationToken cancellationToken = default);
    string BuildFullPath(CmsPage page, IReadOnlyList<CmsPage> allPagesInSite);
    Task TrashPageAsync(Guid pageId, CancellationToken cancellationToken = default);
    Task RestorePageAsync(Guid pageId, CancellationToken cancellationToken = default);
    Task DeletePageAsync(Guid pageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CmsWorkflowBlueprintSummary>> ListWorkflowBlueprintsAsync(CancellationToken cancellationToken = default);
    Task<CmsPage> ApplyWorkflowBlueprintAsync(
        Guid pageId,
        string blueprintKey,
        bool replaceExistingBlocks,
        CancellationToken cancellationToken = default);
}
