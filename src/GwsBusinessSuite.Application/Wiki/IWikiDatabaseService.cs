using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

public interface IWikiDatabaseService
{
    Task<IReadOnlyList<WikiDatabase>> ListDatabasesAsync(bool includeTrashed = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WikiDatabase>> ListTrashedDatabasesAsync(CancellationToken cancellationToken = default);
    Task<WikiDatabase?> GetDatabaseAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default);
    Task<WikiDatabase> CreateDatabaseAsync(string title, Guid? parentWikiPageId, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiDatabase> DuplicateDatabaseAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiDatabaseTemplateSnapshot> CreateTemplateSnapshotAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default);
    Task<WikiDatabase> CreateDatabaseFromTemplateAsync(WikiDatabaseTemplateSnapshot snapshot, Guid? parentWikiPageId, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiDatabase> RenameDatabaseAsync(Guid wikiDatabaseId, string title, string? icon, string performedBy, CancellationToken cancellationToken = default);

    // Soft-delete: unlike TrashPageAsync, this does not cascade to the database's rows - they
    // stay physically untouched and are simply hidden transitively because the database itself
    // is excluded from normal loads, reappearing automatically when the database is restored.
    Task TrashDatabaseAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default);
    Task RestoreDatabaseAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default);
    Task DeleteDatabasePermanentlyAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default);
    Task ReorderDatabaseAsync(Guid wikiDatabaseId, Guid? newParentWikiPageId, int newSortOrder, string performedBy, CancellationToken cancellationToken = default);

    Task<WikiDatabaseProperty> SavePropertyAsync(Guid wikiDatabaseId, WikiDatabasePropertyEditor editor, string performedBy, CancellationToken cancellationToken = default);
    Task DeletePropertyAsync(Guid wikiDatabaseId, Guid propertyId, string performedBy, CancellationToken cancellationToken = default);

    Task<WikiDatabaseRow> SaveRowAsync(Guid wikiDatabaseId, WikiDatabaseRowEditor editor, string performedBy, CancellationToken cancellationToken = default);

    // Row-level soft-delete, independent of TrashDatabaseAsync - for trashing a single row
    // without trashing the whole database. Reference cleanup (removing this row from other
    // rows' Relation properties) only happens on permanent delete, since trash is reversible.
    Task<IReadOnlyList<WikiDatabaseRow>> ListTrashedRowsAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default);
    Task TrashRowAsync(Guid wikiDatabaseId, Guid rowId, string performedBy, CancellationToken cancellationToken = default);
    Task RestoreRowAsync(Guid wikiDatabaseId, Guid rowId, string performedBy, CancellationToken cancellationToken = default);
    Task DeleteRowPermanentlyAsync(Guid wikiDatabaseId, Guid rowId, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiInlineDatabaseSnapshot?> GetInlineDatabaseAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default);
    Task<WikiInlineDatabaseSnapshot> AddInlineRowAsync(Guid wikiDatabaseId, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiInlineDatabaseSnapshot> AddInlineBoardRowAsync(Guid wikiDatabaseId, Guid groupByPropertyId, string? groupOptionId, string? title, string performedBy, CancellationToken cancellationToken = default);
    Task<WikiInlineDatabaseSnapshot> SaveInlineCellAsync(Guid wikiDatabaseId, Guid rowId, Guid propertyId, string? value, string performedBy, CancellationToken cancellationToken = default);

    // Board-drag move: reassigns the row's groupByProperty value and renumbers SortOrder
    // among the rows now sharing that value (mirrors WikiService.ReorderPageAsync's
    // reparent-and-renumber shape, scoped by "same group option" instead of "same parent").
    Task MoveRowAsync(Guid wikiDatabaseId, Guid rowId, Guid groupByPropertyId, string? newGroupOptionId, int newSortOrder, string performedBy, CancellationToken cancellationToken = default);

    Task<WikiDatabaseView> SaveViewAsync(Guid wikiDatabaseId, Guid? viewId, string name, string type, WikiDatabaseViewConfig config, string performedBy, CancellationToken cancellationToken = default);
    Task DeleteViewAsync(Guid wikiDatabaseId, Guid viewId, string performedBy, CancellationToken cancellationToken = default);

    // Bounded DB-snapshot history for a row's page body (WikiDatabaseRowRevision), mirroring
    // IWikiService's page history members exactly.
    Task<IReadOnlyList<WikiRevisionView>> GetRowHistoryAsync(Guid rowId, CancellationToken cancellationToken = default);
    Task<string?> GetRowStructuralDiffAsync(Guid rowId, Guid fromRevisionId, Guid toRevisionId, CancellationToken cancellationToken = default);
    Task<WikiDatabaseRow> RevertRowToRevisionAsync(Guid wikiDatabaseId, Guid rowId, Guid revisionId, string performedBy, CancellationToken cancellationToken = default);
}
