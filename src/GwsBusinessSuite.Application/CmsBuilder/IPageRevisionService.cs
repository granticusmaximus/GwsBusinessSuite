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

    // Part 6.4 - a coarse, section/widget-id-keyed structural diff between two revisions'
    // BlocksJson, mirroring WikiService.GetStructuralDiffAsync / WikiDatabaseService.
    // GetRowStructuralDiffAsync exactly (a plain-text +/-/~ line list, not a generic JSON diff).
    // Returns null if either revision no longer exists.
    Task<string?> GetPageStructuralDiffAsync(Guid fromRevisionId, Guid toRevisionId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid revisionId, CancellationToken cancellationToken = default);

    Task DeleteAllForPageAsync(Guid pageId, CancellationToken cancellationToken = default);
}
