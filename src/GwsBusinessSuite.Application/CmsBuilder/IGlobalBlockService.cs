using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.CmsBuilder;

public interface IGlobalBlockService
{
    Task<IReadOnlyList<GlobalBlock>> ListAsync(Guid siteId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, GlobalBlock>> GetByIdsAsync(
        Guid siteId,
        IEnumerable<Guid> globalBlockIds,
        CancellationToken cancellationToken = default);

    Task<GlobalBlock> CreateWidgetAsync(
        Guid siteId,
        string name,
        LayoutWidget widget,
        CancellationToken cancellationToken = default);

    Task<GlobalBlock> CreateSectionAsync(
        Guid siteId,
        string name,
        LayoutSection section,
        CancellationToken cancellationToken = default);

    Task<GlobalBlock> SyncWidgetAsync(
        Guid siteId,
        LayoutWidget widget,
        CancellationToken cancellationToken = default);

    Task<GlobalBlock> SyncSectionAsync(
        Guid siteId,
        LayoutSection section,
        CancellationToken cancellationToken = default);
}
