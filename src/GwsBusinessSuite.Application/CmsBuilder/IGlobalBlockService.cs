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

    Task RenameAsync(
        Guid siteId,
        Guid globalBlockId,
        string name,
        CancellationToken cancellationToken = default);

    // Phase 3 (per-instance overrides) - which of this Widget-kind block's Props keys each
    // placement may hold its own diverged value for. See LayoutWidget.Overrides and
    // GlobalBlockOverridableFields.CandidatesFor for the offered/candidate keys per widget type.
    Task SetOverridableFieldsAsync(
        Guid siteId,
        Guid globalBlockId,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid siteId,
        Guid globalBlockId,
        CancellationToken cancellationToken = default);
}
