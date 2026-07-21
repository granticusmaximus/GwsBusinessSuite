using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

public interface IWikiDatabaseService
{
    Task<IReadOnlyList<WikiDatabase>> ListDatabasesAsync(CancellationToken cancellationToken = default);
    Task<WikiDatabase?> GetDatabaseAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default);
    Task<WikiDatabase> CreateDatabaseAsync(string title, Guid? parentWikiPageId, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiDatabase> DuplicateDatabaseAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiDatabaseTemplateSnapshot> CreateTemplateSnapshotAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default);
    Task<WikiDatabase> CreateDatabaseFromTemplateAsync(WikiDatabaseTemplateSnapshot snapshot, Guid? parentWikiPageId, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiDatabase> RenameDatabaseAsync(Guid wikiDatabaseId, string title, string? icon, string performedBy, CancellationToken cancellationToken = default);
    Task DeleteDatabaseAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default);
    Task ReorderDatabaseAsync(Guid wikiDatabaseId, Guid? newParentWikiPageId, int newSortOrder, string performedBy, CancellationToken cancellationToken = default);

    Task<WikiDatabaseProperty> SavePropertyAsync(Guid wikiDatabaseId, WikiDatabasePropertyEditor editor, string performedBy, CancellationToken cancellationToken = default);
    Task DeletePropertyAsync(Guid wikiDatabaseId, Guid propertyId, string performedBy, CancellationToken cancellationToken = default);

    Task<WikiDatabaseRow> SaveRowAsync(Guid wikiDatabaseId, WikiDatabaseRowEditor editor, string performedBy, CancellationToken cancellationToken = default);
    Task DeleteRowAsync(Guid wikiDatabaseId, Guid rowId, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiInlineDatabaseSnapshot?> GetInlineDatabaseAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default);
    Task<WikiInlineDatabaseSnapshot> AddInlineRowAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiInlineDatabaseSnapshot> SaveInlineCellAsync(Guid wikiDatabaseId, Guid rowId, Guid propertyId, string? value, string performedBy, CancellationToken cancellationToken = default);

    // Board-drag move: reassigns the row's groupByProperty value and renumbers SortOrder
    // among the rows now sharing that value (mirrors WikiService.ReorderPageAsync's
    // reparent-and-renumber shape, scoped by "same group option" instead of "same parent").
    Task MoveRowAsync(Guid wikiDatabaseId, Guid rowId, Guid groupByPropertyId, string? newGroupOptionId, int newSortOrder, string performedBy, CancellationToken cancellationToken = default);

    Task<WikiDatabaseView> SaveViewAsync(Guid wikiDatabaseId, Guid? viewId, string name, string type, WikiDatabaseViewConfig config, string performedBy, CancellationToken cancellationToken = default);
    Task DeleteViewAsync(Guid wikiDatabaseId, Guid viewId, string performedBy, CancellationToken cancellationToken = default);
}
