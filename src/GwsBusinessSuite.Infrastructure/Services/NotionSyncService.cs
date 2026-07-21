using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// One-way, read-only mirror of a Notion workspace into the Wiki's pages/databases - Notion is
// always the source of truth for Notion-imported content, and re-syncing overwrites local
// edits to it. This app never calls a Notion write endpoint, so there's no risk to the user's
// real Notion workspace either way - only of a local mirror drifting until the next sync.
//
// A Notion "page" object whose parent is a database (parent.type == "database_id") is really
// a database row, not a wiki page - IsDatabaseRow filters those out of the page/database tree
// walk entirely; they're captured instead by the per-database row sync
// (SyncDatabaseSchemaAndRowsAsync), which queries the same objects through the database's own
// query endpoint.
public sealed class NotionSyncService(
    IAppDbContext dbContext,
    INotionService notionService,
    ISecretProtector secretProtector,
    ILogger<NotionSyncService> logger) : INotionSyncService
{
    public async Task<NotionConnectorSettingsView?> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.NotionConnectorSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var (token, isUnreadable) = UnprotectToken(row.IntegrationToken);
        return new NotionConnectorSettingsView
        {
            IntegrationToken = token,
            WorkspaceName = row.WorkspaceName,
            AutoSyncEnabled = row.AutoSyncEnabled,
            LastSyncedAt = row.LastSyncedAt,
            LastSyncImportedCount = row.LastSyncImportedCount,
            LastSyncUpdatedCount = row.LastSyncUpdatedCount,
            LastSyncArchivedCount = row.LastSyncArchivedCount,
            IntegrationTokenUnreadable = isUnreadable
        };
    }

    public async Task<NotionValidationResult> SaveSettingsAsync(NotionConnectorSettingsView settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var row = await dbContext.NotionConnectorSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new NotionConnectorSettings { Id = NotionConnectorSettings.WellKnownId };
            dbContext.NotionConnectorSettings.Add(row);
        }

        var trimmedToken = settings.IntegrationToken.Trim();
        var validation = trimmedToken.Length == 0
            ? new NotionValidationResult(false, "No integration token provided.", null)
            : await notionService.ValidateConnectionAsync(trimmedToken, cancellationToken);

        if (trimmedToken.Length > 0)
        {
            row.IntegrationToken = secretProtector.Protect(trimmedToken);
        }
        if (validation.IsSuccess)
        {
            row.WorkspaceName = validation.WorkspaceName;
        }
        row.AutoSyncEnabled = settings.AutoSyncEnabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = "user";

        await dbContext.SaveChangesAsync(cancellationToken);
        return validation;
    }

    public async Task<NotionSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var settingsRow = await dbContext.NotionConnectorSettings.FirstOrDefaultAsync(cancellationToken);
        if (settingsRow is null || string.IsNullOrWhiteSpace(settingsRow.IntegrationToken))
        {
            return new NotionSyncResult(false, "No Notion integration token configured.", 0, 0, 0);
        }

        var (token, isUnreadable) = UnprotectToken(settingsRow.IntegrationToken);
        if (isUnreadable)
        {
            return new NotionSyncResult(false, "The stored Notion token can no longer be decrypted - re-enter it.", 0, 0, 0);
        }

        try
        {
            var imported = 0;
            var updated = 0;
            var archived = 0;

            // 1. Flat discovery pass - every page/database the integration can see.
            var discovered = new List<JsonElement>();
            string? searchCursor = null;
            do
            {
                var page = await notionService.SearchAsync(token, searchCursor, cancellationToken);
                discovered.AddRange(page.Results);
                searchCursor = page.HasMore ? page.NextCursor : null;
            } while (searchCursor is not null);

            var seenTopLevelNotionIds = new HashSet<string>();
            var notionIdToLocalId = new Dictionary<string, Guid>();
            var notionIdToKind = new Dictionary<string, string>();
            var notionIdToParent = new Dictionary<string, JsonElement>();

            // 2. Reconcile pages/databases by NotionId (upsert, never destructive delete-and-reinsert).
            foreach (var item in discovered)
            {
                if (!item.TryGetProperty("id", out var idElement) || idElement.GetString() is not { } notionId)
                {
                    continue;
                }

                var objectType = item.TryGetProperty("object", out var objectElement) ? objectElement.GetString() : null;
                if (IsDatabaseRow(item, objectType))
                {
                    // A database row, not a standalone page - handled by the per-database row sync below.
                    continue;
                }

                seenTopLevelNotionIds.Add(notionId);
                notionIdToKind[notionId] = objectType ?? "page";
                if (item.TryGetProperty("parent", out var parentElement))
                {
                    notionIdToParent[notionId] = parentElement.Clone();
                }

                var isArchived = IsArchived(item);
                var title = ExtractTitle(item, objectType);

                Guid localId;
                bool wasNew;
                bool becameArchived;
                if (objectType == "database")
                {
                    (localId, wasNew, becameArchived) = await UpsertDatabaseAsync(notionId, title, isArchived, cancellationToken);
                }
                else
                {
                    (localId, wasNew, becameArchived) = await UpsertPageAsync(notionId, title, isArchived, cancellationToken);
                }

                notionIdToLocalId[notionId] = localId;
                if (wasNew) imported++; else updated++;
                if (becameArchived) archived++;
            }
            await dbContext.SaveChangesAsync(cancellationToken);

            // 3. Second pass: wire up hierarchy now that every top-level item has a local id.
            foreach (var (notionId, localId) in notionIdToLocalId)
            {
                Guid? parentWikiPageId = ResolveParentWikiPageId(notionId, notionIdToParent, notionIdToLocalId);

                if (notionIdToKind[notionId] == "database")
                {
                    var database = await dbContext.WikiDatabases.FirstOrDefaultAsync(d => d.Id == localId, cancellationToken);
                    if (database is not null)
                    {
                        database.ParentWikiPageId = parentWikiPageId;
                    }
                }
                else
                {
                    var page = await dbContext.WikiPages.FirstOrDefaultAsync(p => p.Id == localId, cancellationToken);
                    if (page is not null)
                    {
                        page.ParentWikiPageId = parentWikiPageId;
                    }
                }
            }
            await dbContext.SaveChangesAsync(cancellationToken);

            // 4. Per-page block sync.
            foreach (var (notionId, localId) in notionIdToLocalId)
            {
                if (notionIdToKind[notionId] != "database")
                {
                    await SyncPageBlocksAsync(notionId, localId, token, cancellationToken);
                }
            }

            // 5. Per-database property/row sync.
            foreach (var (notionId, localId) in notionIdToLocalId)
            {
                if (notionIdToKind[notionId] == "database")
                {
                    var (rowsImported, rowsUpdated, rowsArchived) = await SyncDatabaseSchemaAndRowsAsync(notionId, localId, token, cancellationToken);
                    imported += rowsImported;
                    updated += rowsUpdated;
                    archived += rowsArchived;
                }
            }

            // 6. Archival - anything from a previous sync no longer returned by this pass.
            archived += await ArchiveMissingAsync(seenTopLevelNotionIds, cancellationToken);

            settingsRow.LastSyncedAt = DateTimeOffset.UtcNow;
            settingsRow.LastSyncImportedCount = imported;
            settingsRow.LastSyncUpdatedCount = updated;
            settingsRow.LastSyncArchivedCount = archived;
            settingsRow.UpdatedAt = DateTimeOffset.UtcNow;
            settingsRow.UpdatedBy = "notion-sync";
            await dbContext.SaveChangesAsync(cancellationToken);

            return new NotionSyncResult(true, "Sync complete.", imported, updated, archived);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notion sync failed");
            return new NotionSyncResult(false, $"Sync failed: {ex.Message}", 0, 0, 0);
        }
    }

    private static bool IsDatabaseRow(JsonElement item, string? objectType) =>
        objectType == "page"
        && item.TryGetProperty("parent", out var parent)
        && parent.TryGetProperty("type", out var parentType)
        && parentType.GetString() == "database_id";

    private static bool IsArchived(JsonElement item) =>
        (item.TryGetProperty("archived", out var archivedElement) && archivedElement.ValueKind == JsonValueKind.True)
        || (item.TryGetProperty("in_trash", out var trashElement) && trashElement.ValueKind == JsonValueKind.True);

    private static string ExtractTitle(JsonElement item, string? objectType)
    {
        if (objectType == "database")
        {
            return item.TryGetProperty("title", out var titleArray)
                ? NonEmptyOrDefault(string.Concat(NotionMapping.MapRichText(titleArray).Select(span => span.Text)))
                : "Untitled Database";
        }

        if (item.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.TryGetProperty("type", out var typeElement) && typeElement.GetString() == "title"
                    && property.Value.TryGetProperty("title", out var titleArray))
                {
                    var text = string.Concat(NotionMapping.MapRichText(titleArray).Select(span => span.Text));
                    if (text.Length > 0)
                    {
                        return text;
                    }
                }
            }
        }

        return "Untitled";
    }

    private static string NonEmptyOrDefault(string value) => string.IsNullOrWhiteSpace(value) ? "Untitled Database" : value;

    private static Guid? ResolveParentWikiPageId(string notionId, IReadOnlyDictionary<string, JsonElement> notionIdToParent, IReadOnlyDictionary<string, Guid> notionIdToLocalId)
    {
        if (!notionIdToParent.TryGetValue(notionId, out var parentDescriptor) || !parentDescriptor.TryGetProperty("type", out var parentTypeElement))
        {
            return null;
        }

        var parentType = parentTypeElement.GetString();
        var parentNotionId = parentType switch
        {
            "page_id" => parentDescriptor.TryGetProperty("page_id", out var pageIdElement) ? pageIdElement.GetString() : null,
            "database_id" => parentDescriptor.TryGetProperty("database_id", out var databaseIdElement) ? databaseIdElement.GetString() : null,
            _ => null
        };

        return parentNotionId is not null && notionIdToLocalId.TryGetValue(parentNotionId, out var parentLocalId)
            ? parentLocalId
            : null;
    }

    private async Task<(Guid LocalId, bool IsNew, bool BecameArchived)> UpsertPageAsync(string notionId, string title, bool isArchived, CancellationToken cancellationToken)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(p => p.NotionId == notionId, cancellationToken);
        var isNew = page is null;
        var wasArchived = page?.NotionArchivedAt is not null;
        if (isNew)
        {
            page = new WikiPage
            {
                Title = title,
                Slug = await GenerateUniqueSlugAsync(title, cancellationToken),
                NotionId = notionId,
                BlocksJson = "[]",
                SortOrder = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "notion-sync"
            };
            await dbContext.WikiPages.AddAsync(page, cancellationToken);
        }

        var contentMetadataChanged = !isNew &&
            (!string.Equals(page!.Title, title, StringComparison.Ordinal)
             || wasArchived != isArchived);

        page!.Title = title;
        page.NotionArchivedAt = isArchived ? (page.NotionArchivedAt ?? DateTimeOffset.UtcNow) : null;
        page.UpdatedAt = DateTimeOffset.UtcNow;
        page.UpdatedBy = "notion-sync";
        if (contentMetadataChanged) page.ContentVersion++;
        return (page.Id, isNew, isArchived && !wasArchived);
    }

    private async Task<(Guid LocalId, bool IsNew, bool BecameArchived)> UpsertDatabaseAsync(string notionId, string title, bool isArchived, CancellationToken cancellationToken)
    {
        var database = await dbContext.WikiDatabases.FirstOrDefaultAsync(d => d.NotionId == notionId, cancellationToken);
        var isNew = database is null;
        var wasArchived = database?.NotionArchivedAt is not null;
        if (isNew)
        {
            var now = DateTimeOffset.UtcNow;
            database = new WikiDatabase
            {
                Title = title,
                NotionId = notionId,
                SortOrder = 0,
                CreatedAt = now,
                CreatedBy = "notion-sync"
            };
            // Seed a Title property + Table view, matching WikiDatabaseService.CreateDatabaseAsync's
            // shape - the property-schema sync below claims this Title property by NotionId instead
            // of creating a second one (a database can only have exactly one).
            database.Properties.Add(new WikiDatabaseProperty
            {
                WikiDatabase = database,
                Name = "Name",
                Type = WikiDatabasePropertyTypes.Title,
                SortOrder = 0,
                CreatedAt = now,
                CreatedBy = "notion-sync"
            });
            database.Views.Add(new WikiDatabaseView
            {
                WikiDatabase = database,
                Name = "Table",
                Type = WikiDatabaseViewTypes.Table,
                SortOrder = 0,
                CreatedAt = now,
                CreatedBy = "notion-sync"
            });
            await dbContext.WikiDatabases.AddAsync(database, cancellationToken);
        }

        database!.Title = title;
        database.NotionArchivedAt = isArchived ? (database.NotionArchivedAt ?? DateTimeOffset.UtcNow) : null;
        database.UpdatedAt = DateTimeOffset.UtcNow;
        database.UpdatedBy = "notion-sync";
        return (database.Id, isNew, isArchived && !wasArchived);
    }

    private async Task<string> GenerateUniqueSlugAsync(string title, CancellationToken cancellationToken)
    {
        var baseSlug = WikiService.CreateSlug(title);
        var existingSlugs = await dbContext.WikiPages.Select(p => p.Slug).ToListAsync(cancellationToken);
        if (!existingSlugs.Contains(baseSlug, StringComparer.OrdinalIgnoreCase))
        {
            return baseSlug;
        }

        var counter = 2;
        while (true)
        {
            var candidate = $"{baseSlug}-{counter}";
            if (!existingSlugs.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }

            counter++;
        }
    }

    // Recursively walks a page's block tree and overwrites BlocksJson with the mapped result.
    // Deliberately does not create a WikiPageRevision snapshot for sync-driven content changes
    // (only interactive Save does) - an hourly background sync would otherwise flood the
    // bounded 20-revision history with sync noise, crowding out actual authored edits.
    private async Task SyncPageBlocksAsync(string notionPageId, Guid wikiPageId, string token, CancellationToken cancellationToken)
    {
        var blocks = new List<WikiBlock>();
        await AppendBlockChildrenAsync(notionPageId, 0, token, blocks, cancellationToken);

        var page = await dbContext.WikiPages.FirstOrDefaultAsync(p => p.Id == wikiPageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        var blocksJson = WikiBlockJson.Serialize(blocks);
        if (!string.Equals(page.BlocksJson, blocksJson, StringComparison.Ordinal))
        {
            page.BlocksJson = blocksJson;
            page.ContentVersion++;
        }
        page.UpdatedAt = DateTimeOffset.UtcNow;
        page.UpdatedBy = "notion-sync";
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task AppendBlockChildrenAsync(string notionBlockId, int indentLevel, string token, List<WikiBlock> blocks, CancellationToken cancellationToken)
    {
        var children = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await notionService.GetBlockChildrenAsync(token, notionBlockId, cursor, cancellationToken);
            children.AddRange(page.Results);
            cursor = page.HasMore ? page.NextCursor : null;
        } while (cursor is not null);

        foreach (var child in children)
        {
            var type = child.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
            var hasChildren = child.TryGetProperty("has_children", out var hasChildrenElement) && hasChildrenElement.ValueKind == JsonValueKind.True;
            var childId = child.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;

            if (type == "table")
            {
                var rowChildren = new List<JsonElement>();
                string? rowCursor = null;
                do
                {
                    var rowPage = await notionService.GetBlockChildrenAsync(token, childId, rowCursor, cancellationToken);
                    rowChildren.AddRange(rowPage.Results);
                    rowCursor = rowPage.HasMore ? rowPage.NextCursor : null;
                } while (rowCursor is not null);

                blocks.Add(NotionMapping.MapTable(child, rowChildren, indentLevel));
                continue;
            }

            if (NotionMapping.IsPageTreeBlock(type))
            {
                // child_page/child_database - already synced as their own top-level page/database.
                continue;
            }

            if (NotionMapping.IsFlattenedWrapper(type))
            {
                if (hasChildren && childId.Length > 0)
                {
                    // Pure layout wrapper with no GWS equivalent - its children are imported in
                    // place, one after another, at the same indent level as the wrapper itself.
                    await AppendBlockChildrenAsync(childId, indentLevel, token, blocks, cancellationToken);
                }
                continue;
            }

            var mapped = NotionMapping.MapBlock(child, indentLevel, unsupportedType =>
                logger.LogWarning("Notion sync: skipping unsupported block type {BlockType}", unsupportedType));
            if (mapped is null)
            {
                continue;
            }

            blocks.Add(mapped);
            if (hasChildren && childId.Length > 0)
            {
                await AppendBlockChildrenAsync(childId, indentLevel + 1, token, blocks, cancellationToken);
            }
        }
    }

    private async Task<(int Imported, int Updated, int Archived)> SyncDatabaseSchemaAndRowsAsync(string notionDatabaseId, Guid wikiDatabaseId, string token, CancellationToken cancellationToken)
    {
        var schema = await notionService.GetDatabaseAsync(token, notionDatabaseId, cancellationToken);
        if (schema is null)
        {
            return (0, 0, 0);
        }

        var notionPropertyIdToLocal = new Dictionary<string, (Guid Id, string Type)>();
        if (schema.Value.TryGetProperty("properties", out var propertiesElement) && propertiesElement.ValueKind == JsonValueKind.Object)
        {
            var sortOrder = 0;
            foreach (var propertyField in propertiesElement.EnumerateObject())
            {
                var propertySchema = propertyField.Value;
                var notionPropertyId = propertySchema.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
                var notionPropertyType = propertySchema.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
                if (notionPropertyId.Length == 0)
                {
                    continue;
                }

                var localType = NotionMapping.MapPropertyType(notionPropertyType);
                var existing = await dbContext.WikiDatabaseProperties.FirstOrDefaultAsync(
                    p => p.WikiDatabaseId == wikiDatabaseId && p.NotionId == notionPropertyId, cancellationToken);

                // A database always starts with a locally-seeded, NotionId-less Title property
                // (see UpsertDatabaseAsync) - Notion's own title property claims that row
                // instead of creating a second Title property, since exactly one is allowed.
                if (existing is null && localType == WikiDatabasePropertyTypes.Title)
                {
                    existing = await dbContext.WikiDatabaseProperties.FirstOrDefaultAsync(
                        p => p.WikiDatabaseId == wikiDatabaseId && p.Type == WikiDatabasePropertyTypes.Title && p.NotionId == null, cancellationToken);
                }

                var isNew = existing is null;
                var property = existing ?? new WikiDatabaseProperty
                {
                    WikiDatabaseId = wikiDatabaseId,
                    Name = propertyField.Name,
                    Type = localType,
                    SortOrder = sortOrder,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CreatedBy = "notion-sync"
                };

                property.Name = propertyField.Name;
                property.NotionId = notionPropertyId;
                if (localType is WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.MultiSelect)
                {
                    property.ConfigJson = WikiDatabasePropertyConfig.Serialize(NotionMapping.MapPropertyOptions(propertySchema, notionPropertyType));
                }
                property.UpdatedAt = DateTimeOffset.UtcNow;
                property.UpdatedBy = "notion-sync";

                if (isNew)
                {
                    await dbContext.WikiDatabaseProperties.AddAsync(property, cancellationToken);
                }

                notionPropertyIdToLocal[notionPropertyId] = (property.Id, property.Type);
                sortOrder++;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var rows = new List<JsonElement>();
        string? rowCursor = null;
        do
        {
            var page = await notionService.QueryDatabaseAsync(token, notionDatabaseId, rowCursor, cancellationToken);
            rows.AddRange(page.Results);
            rowCursor = page.HasMore ? page.NextCursor : null;
        } while (rowCursor is not null);

        var nextSortOrder = (await dbContext.WikiDatabaseRows
            .Where(r => r.WikiDatabaseId == wikiDatabaseId)
            .Select(r => (int?)r.SortOrder)
            .MaxAsync(cancellationToken) ?? -1) + 1;

        var imported = 0;
        var updated = 0;
        var archived = 0;
        var seenRowNotionIds = new HashSet<string>();
        foreach (var rowElement in rows)
        {
            if (!rowElement.TryGetProperty("id", out var rowIdElement) || rowIdElement.GetString() is not { } rowNotionId)
            {
                continue;
            }

            seenRowNotionIds.Add(rowNotionId);
            var row = await dbContext.WikiDatabaseRows.FirstOrDefaultAsync(r => r.WikiDatabaseId == wikiDatabaseId && r.NotionId == rowNotionId, cancellationToken);
            var isNew = row is null;
            var wasArchived = row?.NotionArchivedAt is not null;
            row ??= new WikiDatabaseRow
            {
                WikiDatabaseId = wikiDatabaseId,
                NotionId = rowNotionId,
                SortOrder = nextSortOrder++,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "notion-sync"
            };

            var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
            if (rowElement.TryGetProperty("properties", out var rowProperties) && rowProperties.ValueKind == JsonValueKind.Object)
            {
                foreach (var propertyValue in rowProperties.EnumerateObject())
                {
                    var notionPropertyId = propertyValue.Value.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    if (notionPropertyId is not null && notionPropertyIdToLocal.TryGetValue(notionPropertyId, out var local))
                    {
                        NotionMapping.ApplyPropertyValue(values, local.Id, local.Type, propertyValue.Value);
                    }
                }
            }

            row.PropertyValuesJson = WikiPropertyValues.Serialize(values);
            var rowBlocks = new List<WikiBlock>();
            await AppendBlockChildrenAsync(rowNotionId, 0, token, rowBlocks, cancellationToken);
            row.BlocksJson = WikiBlockJson.Serialize(rowBlocks);
            row.NotionArchivedAt = IsArchived(rowElement) ? (row.NotionArchivedAt ?? DateTimeOffset.UtcNow) : null;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = "notion-sync";

            if (isNew)
            {
                await dbContext.WikiDatabaseRows.AddAsync(row, cancellationToken);
                imported++;
            }
            else
            {
                updated++;
            }
            if (row.NotionArchivedAt is not null && !wasArchived)
            {
                archived++;
            }
        }

        var existingRows = await dbContext.WikiDatabaseRows
            .Where(r => r.WikiDatabaseId == wikiDatabaseId && r.NotionId != null)
            .ToListAsync(cancellationToken);
        foreach (var existingRow in existingRows)
        {
            if (existingRow.NotionId is { } notionId && !seenRowNotionIds.Contains(notionId) && existingRow.NotionArchivedAt is null)
            {
                existingRow.NotionArchivedAt = DateTimeOffset.UtcNow;
                archived++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return (imported, updated, archived);
    }

    private async Task<int> ArchiveMissingAsync(HashSet<string> seenTopLevelNotionIds, CancellationToken cancellationToken)
    {
        var archived = 0;

        var syncedPages = await dbContext.WikiPages.Where(p => p.NotionId != null).ToListAsync(cancellationToken);
        foreach (var page in syncedPages)
        {
            if (page.NotionId is { } notionId && !seenTopLevelNotionIds.Contains(notionId) && page.NotionArchivedAt is null)
            {
                page.NotionArchivedAt = DateTimeOffset.UtcNow;
                page.ContentVersion++;
                archived++;
            }
        }

        var syncedDatabases = await dbContext.WikiDatabases.Where(d => d.NotionId != null).ToListAsync(cancellationToken);
        foreach (var database in syncedDatabases)
        {
            if (database.NotionId is { } notionId && !seenTopLevelNotionIds.Contains(notionId) && database.NotionArchivedAt is null)
            {
                database.NotionArchivedAt = DateTimeOffset.UtcNow;
                archived++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return archived;
    }

    private (string Token, bool IsUnreadable) UnprotectToken(string storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return (string.Empty, false);
        }

        try
        {
            return (secretProtector.Unprotect(storedValue), false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to decrypt stored Notion integration token. The key ring may have changed since it was saved.");
            return (string.Empty, true);
        }
    }
}
