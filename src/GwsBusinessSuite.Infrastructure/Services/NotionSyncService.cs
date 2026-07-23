using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Imports a selectable Notion workspace surface into Sentinel. Import remains the safe
// default; an explicit two-way setting and a separate write acknowledgement are both
// required before the user can push a Sentinel page back to Notion.
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

        var (_, isUnreadable) = UnprotectToken(row.IntegrationToken);
        return new NotionConnectorSettingsView
        {
            // Never return the decrypted credential to the Blazor client. A blank input means
            // "keep the stored token"; entering a value explicitly replaces it.
            IntegrationToken = string.Empty,
            HasStoredIntegrationToken = !string.IsNullOrWhiteSpace(row.IntegrationToken),
            WorkspaceName = row.WorkspaceName,
            AutoSyncEnabled = row.AutoSyncEnabled,
            SyncDirection = row.SyncDirection,
            SelectedNotionIds = string.Join(", ", DeserializeSelectedIds(row.SelectedNotionIdsJson)),
            AllowTwoWayWrites = row.AllowTwoWayWrites,
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

        var suppliedToken = settings.IntegrationToken.Trim();
        var validationToken = suppliedToken;
        if (validationToken.Length == 0 && !string.IsNullOrWhiteSpace(row.IntegrationToken))
        {
            var (storedToken, isUnreadable) = UnprotectToken(row.IntegrationToken);
            if (isUnreadable)
            {
                return new NotionValidationResult(
                    false,
                    "The stored Notion token can no longer be decrypted. Enter a replacement token.",
                    null);
            }
            validationToken = storedToken;
        }

        var validation = validationToken.Length == 0
            ? new NotionValidationResult(false, "No integration token provided.", null)
            : await notionService.ValidateConnectionAsync(validationToken, cancellationToken);

        if (suppliedToken.Length > 0 && validation.IsSuccess)
        {
            row.IntegrationToken = secretProtector.Protect(suppliedToken);
        }
        if (validation.IsSuccess)
        {
            row.WorkspaceName = validation.WorkspaceName;
        }
        row.AutoSyncEnabled = settings.AutoSyncEnabled;
        row.SyncDirection = string.Equals(settings.SyncDirection, "twoWay", StringComparison.OrdinalIgnoreCase) ? "twoWay" : "import";
        row.SelectedNotionIdsJson = JsonSerializer.Serialize(ParseSelectedIds(settings.SelectedNotionIds));
        row.AllowTwoWayWrites = settings.AllowTwoWayWrites && row.SyncDirection == "twoWay";
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
            var contentBlocks = 0;
            var markdownFallbackPages = 0;
            var emptyContentPages = 0;

            // 1. Flat discovery pass - every page/database the integration can see.
            var discovered = new List<JsonElement>();
            string? searchCursor = null;
            do
            {
                var page = await notionService.SearchAsync(token, searchCursor, cancellationToken);
                discovered.AddRange(page.Results);
                searchCursor = page.HasMore ? page.NextCursor : null;
            } while (searchCursor is not null);

            var selectedIds = DeserializeSelectedIds(settingsRow.SelectedNotionIdsJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (discovered.Count == 0)
            {
                return new NotionSyncResult(
                    false,
                    "Connected to Notion, but no shared pages or databases are accessible. In Notion, open a top-level page, choose Connections, add the Sentinel integration, then sync again.",
                    0,
                    0,
                    0);
            }

            if (selectedIds.Count > 0)
            {
                discovered = IncludeSelectedContentAndDescendants(discovered, selectedIds);
                if (discovered.Count == 0)
                {
                    return new NotionSyncResult(
                        false,
                        "Notion content is accessible, but none matches the selected page/data source IDs. Clear the ID field to import everything shared, or verify those IDs.",
                        0,
                        0,
                        0);
                }
            }

            var seenTopLevelNotionIds = new HashSet<string>();
            var notionIdToLocalId = new Dictionary<string, Guid>();
            var notionIdToKind = new Dictionary<string, string>();
            var notionIdToParent = new Dictionary<string, JsonElement>();
            // Reserve slugs for both persisted and newly tracked pages. Querying the database
            // inside each upsert does not include Added entities, so duplicate Notion titles in
            // one discovery batch would otherwise violate WikiPages' unique slug index.
            var reservedPageSlugs = (await dbContext.WikiPages
                    .AsNoTracking()
                    .Select(page => page.Slug)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
                if (objectType is "database" or "data_source")
                {
                    (localId, wasNew, becameArchived) = await UpsertDatabaseAsync(notionId, title, isArchived, cancellationToken);
                }
                else
                {
                    (localId, wasNew, becameArchived) = await UpsertPageAsync(
                        notionId,
                        title,
                        isArchived,
                        reservedPageSlugs,
                        cancellationToken);
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

                if (notionIdToKind[notionId] is "database" or "data_source")
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
                if (notionIdToKind[notionId] is not ("database" or "data_source"))
                {
                    var pageContent = await SyncPageBlocksAsync(notionId, localId, token, cancellationToken);
                    contentBlocks += pageContent.BlockCount;
                    if (pageContent.UsedMarkdownFallback) markdownFallbackPages++;
                    if (pageContent.BlockCount == 0) emptyContentPages++;
                    await SyncPageCommentsAsync(notionId, localId, token, cancellationToken);
                }
            }

            // 5. Per-database property/row sync.
            foreach (var (notionId, localId) in notionIdToLocalId)
            {
                if (notionIdToKind[notionId] is "database" or "data_source")
                {
                    var databaseContent = await SyncDatabaseSchemaAndRowsAsync(
                        notionId,
                        localId,
                        token,
                        cancellationToken);
                    imported += databaseContent.Imported;
                    updated += databaseContent.Updated;
                    archived += databaseContent.Archived;
                    contentBlocks += databaseContent.ContentBlocks;
                    markdownFallbackPages += databaseContent.MarkdownFallbackPages;
                    emptyContentPages += databaseContent.EmptyContentPages;
                }
            }

            // 6. Archival - anything from a previous sync no longer returned by this pass.
            if (selectedIds.Count == 0)
            {
                archived += await ArchiveMissingAsync(seenTopLevelNotionIds, cancellationToken);
            }

            settingsRow.LastSyncedAt = DateTimeOffset.UtcNow;
            settingsRow.LastSyncImportedCount = imported;
            settingsRow.LastSyncUpdatedCount = updated;
            settingsRow.LastSyncArchivedCount = archived;
            settingsRow.UpdatedAt = DateTimeOffset.UtcNow;
            settingsRow.UpdatedBy = "notion-sync";
            await dbContext.SaveChangesAsync(cancellationToken);

            var message = $"Sync complete. Imported {contentBlocks} content block{(contentBlocks == 1 ? string.Empty : "s")}.";
            if (markdownFallbackPages > 0)
            {
                message += $" Recovered {markdownFallbackPages} page{(markdownFallbackPages == 1 ? string.Empty : "s")} through Notion's full-page content endpoint.";
            }
            if (emptyContentPages > 0)
            {
                message += $" {emptyContentPages} page{(emptyContentPages == 1 ? string.Empty : "s")} returned no readable content; confirm the integration has Read content capability and access to those pages.";
            }

            return new NotionSyncResult(true, message, imported, updated, archived);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Notion sync failed while saving imported entities");
            var entityTypes = ex.Entries
                .Select(entry => entry.Metadata.ClrType.Name)
                .Distinct(StringComparer.Ordinal)
                .Order()
                .ToArray();
            var entityLabel = entityTypes.Length == 0
                ? "imported content"
                : string.Join(", ", entityTypes);
            var providerMessage = ex.GetBaseException().Message;
            return new NotionSyncResult(
                false,
                $"Sync failed while saving {entityLabel}: {providerMessage}",
                0,
                0,
                0);
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
        && parentType.GetString() is "database_id" or "data_source_id";

    private static bool IsArchived(JsonElement item) =>
        (item.TryGetProperty("archived", out var archivedElement) && archivedElement.ValueKind == JsonValueKind.True)
        || (item.TryGetProperty("in_trash", out var trashElement) && trashElement.ValueKind == JsonValueKind.True);

    private static string ExtractTitle(JsonElement item, string? objectType)
    {
        if (objectType is "database" or "data_source")
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

    private static List<JsonElement> IncludeSelectedContentAndDescendants(
        IReadOnlyList<JsonElement> discovered,
        IReadOnlySet<string> selectedIds)
    {
        var includedIds = selectedIds
            .Select(NormalizeNotionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Search returns both parent and child pages shared with the integration. A selected
        // top-level page therefore defines a subtree, not a single object. Expand that subtree
        // before filtering so child_page references still resolve to real imported pages with
        // their own block content.
        bool added;
        do
        {
            added = false;
            foreach (var item in discovered)
            {
                var id = GetNotionId(item);
                var parentId = GetParentNotionId(item);
                if (id is null || parentId is null || !includedIds.Contains(NormalizeNotionId(parentId)))
                {
                    continue;
                }

                added |= includedIds.Add(NormalizeNotionId(id));
            }
        } while (added);

        return discovered
            .Where(item => GetNotionId(item) is { } id && includedIds.Contains(NormalizeNotionId(id)))
            .ToList();
    }

    private static string? GetNotionId(JsonElement item) =>
        item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;

    private static string? GetParentNotionId(JsonElement item)
    {
        if (!item.TryGetProperty("parent", out var parent)
            || !parent.TryGetProperty("type", out var parentTypeElement))
        {
            return null;
        }

        var parentType = parentTypeElement.GetString();
        return parentType switch
        {
            "page_id" => parent.TryGetProperty("page_id", out var pageId) ? pageId.GetString() : null,
            "database_id" => parent.TryGetProperty("database_id", out var databaseId) ? databaseId.GetString() : null,
            "data_source_id" => parent.TryGetProperty("data_source_id", out var dataSourceId) ? dataSourceId.GetString() : null,
            _ => null
        };
    }

    private static string NormalizeNotionId(string value) =>
        Guid.TryParse(value.Trim(), out var id) ? id.ToString("N") : value.Trim();

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
            "data_source_id" => parentDescriptor.TryGetProperty("data_source_id", out var dataSourceIdElement) ? dataSourceIdElement.GetString() : null,
            _ => null
        };

        return parentNotionId is not null && notionIdToLocalId.TryGetValue(parentNotionId, out var parentLocalId)
            ? parentLocalId
            : null;
    }

    private async Task<(Guid LocalId, bool IsNew, bool BecameArchived)> UpsertPageAsync(
        string notionId,
        string title,
        bool isArchived,
        HashSet<string> reservedSlugs,
        CancellationToken cancellationToken)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(p => p.NotionId == notionId, cancellationToken);
        var isNew = page is null;
        var wasArchived = page?.NotionArchivedAt is not null;
        if (isNew)
        {
            page = new WikiPage
            {
                Title = title,
                Slug = ReserveUniqueSlug(title, reservedSlugs),
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

    private static string ReserveUniqueSlug(string title, HashSet<string> reservedSlugs)
    {
        var baseSlug = WikiService.CreateSlug(title);
        if (reservedSlugs.Add(baseSlug))
        {
            return baseSlug;
        }

        for (var counter = 2; ; counter++)
        {
            var candidate = $"{baseSlug}-{counter}";
            if (reservedSlugs.Add(candidate))
            {
                return candidate;
            }
        }
    }

    // Recursively walks a page's block tree and overwrites BlocksJson with the mapped result.
    // Deliberately does not create a WikiPageRevision snapshot for sync-driven content changes
    // (only interactive Save does) - an hourly background sync would otherwise flood the
    // bounded 20-revision history with sync noise, crowding out actual authored edits.
    private async Task<PageContentSyncResult> SyncPageBlocksAsync(
        string notionPageId,
        Guid wikiPageId,
        string token,
        CancellationToken cancellationToken)
    {
        var pageContent = await LoadNotionPageBlocksAsync(notionPageId, token, cancellationToken);

        var page = await dbContext.WikiPages.FirstOrDefaultAsync(p => p.Id == wikiPageId, cancellationToken);
        if (page is null)
        {
            return pageContent;
        }

        if (!pageContent.ContentUnavailable)
        {
            var blocksJson = WikiBlockJson.Serialize(pageContent.Blocks);
            if (!string.Equals(page.BlocksJson, blocksJson, StringComparison.Ordinal))
            {
                page.BlocksJson = blocksJson;
                page.ContentVersion++;
            }
        }
        page.UpdatedAt = DateTimeOffset.UtcNow;
        page.UpdatedBy = "notion-sync";
        await dbContext.SaveChangesAsync(cancellationToken);
        return pageContent;
    }

    private async Task<PageContentSyncResult> LoadNotionPageBlocksAsync(
        string notionPageId,
        string token,
        CancellationToken cancellationToken)
    {
        var blocks = new List<WikiBlock>();
        var structuredContentUnavailable = false;
        try
        {
            await AppendBlockChildrenAsync(notionPageId, 0, token, blocks, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is
                                             System.Net.HttpStatusCode.NotFound or
                                             System.Net.HttpStatusCode.Forbidden)
        {
            // Search can return page metadata even when a particular child-content endpoint
            // is unavailable. Do not abort the remaining workspace; recover this page through
            // Notion's full-page endpoint instead.
            logger.LogWarning(
                ex,
                "Structured Notion content was unavailable for page {NotionPageId}; trying full-page Markdown.",
                notionPageId);
            blocks.Clear();
            structuredContentUnavailable = true;
        }

        if (blocks.Count > 0)
        {
            return new PageContentSyncResult(blocks, false, false);
        }

        try
        {
            var markdownPage = await notionService.GetPageMarkdownAsync(
                token,
                notionPageId,
                cancellationToken);
            if (markdownPage is null || string.IsNullOrWhiteSpace(markdownPage.Markdown))
            {
                logger.LogWarning(
                    "Notion page {NotionPageId} returned no structured blocks or full-page Markdown content.",
                    notionPageId);
                return new PageContentSyncResult(blocks, false, structuredContentUnavailable);
            }

            blocks.AddRange(WikiBlockJson.FromMarkdown(markdownPage.Markdown));
            logger.LogInformation(
                "Recovered {BlockCount} Sentinel blocks from full-page Markdown for Notion page {NotionPageId}.",
                blocks.Count,
                notionPageId);
            if (markdownPage.Truncated || markdownPage.UnknownBlockIds.Count > 0)
            {
                logger.LogWarning(
                    "Notion page {NotionPageId} returned truncated Markdown with {UnknownBlockCount} unknown blocks.",
                    notionPageId,
                    markdownPage.UnknownBlockIds.Count);
            }
            return new PageContentSyncResult(blocks, blocks.Count > 0, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not retrieve full-page Markdown fallback for Notion page {NotionPageId}.",
                notionPageId);
            return new PageContentSyncResult(blocks, false, structuredContentUnavailable);
        }
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

            if (type is "meeting_notes" or "transcription")
            {
                await AppendMeetingNotesAsync(child, childId, hasChildren, indentLevel, token, blocks, cancellationToken);
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
                // Notion can return unknown container types as "unsupported". The wrapper
                // itself cannot be represented, but its ordinary paragraph/list children can
                // still be retrieved and must not be discarded with it.
                if (hasChildren && childId.Length > 0)
                {
                    await AppendBlockChildrenAsync(childId, indentLevel, token, blocks, cancellationToken);
                }
                continue;
            }

            mapped = await PersistNotionFileAsync(mapped, cancellationToken);
            blocks.Add(mapped);
            if (hasChildren && childId.Length > 0)
            {
                await AppendBlockChildrenAsync(childId, indentLevel + 1, token, blocks, cancellationToken);
            }
        }
    }

    private async Task AppendMeetingNotesAsync(
        JsonElement block,
        string blockId,
        bool hasChildren,
        int indentLevel,
        string token,
        List<WikiBlock> blocks,
        CancellationToken cancellationToken)
    {
        var type = block.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? "meeting_notes"
            : "meeting_notes";
        var body = block.TryGetProperty(type, out var bodyElement) ? bodyElement : default;
        var title = body.ValueKind == JsonValueKind.Object
                    && body.TryGetProperty("title", out var titleElement)
            ? NotionMapping.MapRichText(titleElement)
            : [];

        if (title.Count > 0)
        {
            blocks.Add(new WikiBlock(
                Guid.NewGuid(),
                WikiBlockTypes.Callout,
                indentLevel,
                title,
                new Dictionary<string, string> { ["icon"] = "🎙️" }));
        }

        var sections = new List<(string Label, string BlockId)>();
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("children", out var children)
            && children.ValueKind == JsonValueKind.Object)
        {
            AddMeetingNoteSection(sections, children, "summary_block_id", "Summary");
            AddMeetingNoteSection(sections, children, "notes_block_id", "Notes");
            AddMeetingNoteSection(sections, children, "transcript_block_id", "Transcript");
        }

        foreach (var (label, sectionBlockId) in sections
                     .DistinctBy(section => section.BlockId, StringComparer.OrdinalIgnoreCase))
        {
            blocks.Add(new WikiBlock(
                Guid.NewGuid(),
                WikiBlockTypes.Heading3,
                indentLevel + 1,
                [new WikiRichTextSpan(label)],
                new Dictionary<string, string>()));
            await AppendBlockChildrenAsync(
                sectionBlockId,
                indentLevel + 1,
                token,
                blocks,
                cancellationToken);
        }

        // Older API responses may expose the meeting-note content as ordinary children
        // instead of the 2026-03-11 section pointers.
        if (sections.Count == 0 && hasChildren && blockId.Length > 0)
        {
            await AppendBlockChildrenAsync(blockId, indentLevel + 1, token, blocks, cancellationToken);
        }
    }

    private static void AddMeetingNoteSection(
        ICollection<(string Label, string BlockId)> sections,
        JsonElement children,
        string propertyName,
        string label)
    {
        if (children.TryGetProperty(propertyName, out var idElement)
            && idElement.ValueKind == JsonValueKind.String
            && idElement.GetString() is { Length: > 0 } blockId)
        {
            sections.Add((label, blockId));
        }
    }

    private async Task<WikiBlock> PersistNotionFileAsync(
        WikiBlock block,
        CancellationToken cancellationToken)
    {
        if (!block.Props.TryGetValue("notionSourceType", out var sourceType)
            || !string.Equals(sourceType, "file", StringComparison.OrdinalIgnoreCase)
            || !block.Props.TryGetValue("notionBlockId", out var notionBlockId)
            || string.IsNullOrWhiteSpace(notionBlockId)
            || !block.Props.TryGetValue("url", out var sourceUrl)
            || string.IsNullOrWhiteSpace(sourceUrl))
        {
            return block;
        }

        var importedFile = dbContext.SentinelImportedFiles.Local
            .FirstOrDefault(file => file.NotionBlockId == notionBlockId)
            ?? await dbContext.SentinelImportedFiles
                .FirstOrDefaultAsync(file => file.NotionBlockId == notionBlockId, cancellationToken);

        try
        {
            var download = await notionService.DownloadFileAsync(sourceUrl, cancellationToken);
            if (importedFile is null)
            {
                importedFile = new SentinelImportedFile
                {
                    NotionBlockId = notionBlockId,
                    FileName = download.FileName,
                    ContentType = download.ContentType,
                    Content = download.Content,
                    SizeBytes = download.Content.LongLength,
                    CreatedBy = "notion-sync"
                };
                await dbContext.SentinelImportedFiles.AddAsync(importedFile, cancellationToken);
            }
            else
            {
                importedFile.FileName = download.FileName;
                importedFile.ContentType = download.ContentType;
                importedFile.Content = download.Content;
                importedFile.SizeBytes = download.Content.LongLength;
                importedFile.UpdatedAt = DateTimeOffset.UtcNow;
                importedFile.UpdatedBy = "notion-sync";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not cache Notion file block {NotionBlockId}; retaining any existing durable copy.", notionBlockId);
            if (importedFile is null)
            {
                return block;
            }
        }

        var props = block.Props.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        props["url"] = $"/admin/sentinel/files/{importedFile.Id}";
        props["fileName"] = importedFile.FileName;
        props.Remove("notionSourceType");
        return block with { Props = props };
    }

    private async Task<DatabaseContentSyncResult> SyncDatabaseSchemaAndRowsAsync(
        string notionDatabaseId,
        Guid wikiDatabaseId,
        string token,
        CancellationToken cancellationToken)
    {
        var schema = await notionService.GetDatabaseAsync(token, notionDatabaseId, cancellationToken);
        if (schema is null)
        {
            return new DatabaseContentSyncResult(0, 0, 0, 0, 0, 0);
        }

        await SyncDatabaseViewsAsync(schema.Value, wikiDatabaseId, token, cancellationToken);

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
        var contentBlocks = 0;
        var markdownFallbackPages = 0;
        var emptyContentPages = 0;
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
            var rowContent = await LoadNotionPageBlocksAsync(rowNotionId, token, cancellationToken);
            if (!rowContent.ContentUnavailable)
            {
                row.BlocksJson = WikiBlockJson.Serialize(rowContent.Blocks);
            }
            contentBlocks += rowContent.BlockCount;
            if (rowContent.UsedMarkdownFallback) markdownFallbackPages++;
            if (rowContent.BlockCount == 0) emptyContentPages++;
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
        return new DatabaseContentSyncResult(
            imported,
            updated,
            archived,
            contentBlocks,
            markdownFallbackPages,
            emptyContentPages);
    }

    private async Task SyncDatabaseViewsAsync(JsonElement schema, Guid wikiDatabaseId, string token, CancellationToken cancellationToken)
    {
        if (!schema.TryGetProperty("views", out var viewsElement) || viewsElement.ValueKind != JsonValueKind.Array) return;

        var order = 0;
        foreach (var viewElement in viewsElement.EnumerateArray())
        {
            var notionId = viewElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(notionId)) continue;
            var remote = await notionService.GetViewAsync(token, notionId, cancellationToken)
                ?? viewElement;
            var name = remote.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var type = remote.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            var existing = await dbContext.WikiDatabaseViews.FirstOrDefaultAsync(v => v.WikiDatabaseId == wikiDatabaseId && v.NotionId == notionId, cancellationToken);
            var isNew = existing is null;
            existing ??= new WikiDatabaseView
            {
                WikiDatabaseId = wikiDatabaseId,
                Name = name ?? "Notion view",
                Type = MapViewType(type),
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "notion-sync"
            };
            existing.NotionId = notionId;
            existing.Name = string.IsNullOrWhiteSpace(name) ? "Notion view" : name;
            existing.Type = MapViewType(type);
            existing.SortOrder = order++;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedBy = "notion-sync";
            if (isNew) dbContext.WikiDatabaseViews.Add(existing);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string MapViewType(string? type) => type switch
    {
        "board" => WikiDatabaseViewTypes.Board,
        "timeline" => WikiDatabaseViewTypes.Timeline,
        "calendar" => WikiDatabaseViewTypes.Calendar,
        "list" => WikiDatabaseViewTypes.List,
        "gallery" => WikiDatabaseViewTypes.Gallery,
        "chart" => WikiDatabaseViewTypes.Chart,
        "form" => WikiDatabaseViewTypes.Form,
        "map" => WikiDatabaseViewTypes.Map,
        "feed" => WikiDatabaseViewTypes.Feed,
        "dashboard" => WikiDatabaseViewTypes.Dashboard,
        _ => WikiDatabaseViewTypes.Table
    };

    private async Task SyncPageCommentsAsync(string notionPageId, Guid wikiPageId, string token, CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            NotionPage commentPage;
            try
            {
                commentPage = await notionService.ListCommentsAsync(
                    token,
                    notionPageId,
                    cursor,
                    cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is
                                                 System.Net.HttpStatusCode.NotFound or
                                                 System.Net.HttpStatusCode.Forbidden)
            {
                // Comments are optional content and use a separate Notion capability. A page
                // import must not fail because that endpoint is unavailable.
                logger.LogWarning(
                    ex,
                    "Notion comments are unavailable for page {NotionPageId}; page content was still imported.",
                    notionPageId);
                return;
            }

            foreach (var item in commentPage.Results)
            {
                var notionId = item.TryGetProperty("id", out var id) ? id.GetString() : null;
                if (string.IsNullOrWhiteSpace(notionId) || await dbContext.SentinelDiscussionComments.AnyAsync(c => c.NotionId == notionId, cancellationToken)) continue;
                var body = item.TryGetProperty("rich_text", out var richText)
                    ? string.Concat(NotionMapping.MapRichText(richText).Select(span => span.Text))
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(body)) continue;
                var discussion = new SentinelDiscussion
                {
                    WikiPageId = wikiPageId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CreatedBy = "notion-sync"
                };
                discussion.Comments.Add(new SentinelDiscussionComment
                {
                    Body = body,
                    NotionId = notionId,
                    CreatedAt = item.TryGetProperty("created_time", out var created) && DateTimeOffset.TryParse(created.GetString(), out var createdAt) ? createdAt : DateTimeOffset.UtcNow,
                    CreatedBy = "notion-sync"
                });
                dbContext.SentinelDiscussions.Add(discussion);
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            cursor = commentPage.HasMore ? commentPage.NextCursor : null;
        } while (cursor is not null);
    }

    public async Task<NotionSyncResult> PushPageAsync(Guid wikiPageId, CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.NotionConnectorSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null || settings.SyncDirection != "twoWay" || !settings.AllowTwoWayWrites)
            return new(false, "Two-way sync and the write acknowledgement must both be enabled.", 0, 0, 0);
        var page = await dbContext.WikiPages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == wikiPageId, cancellationToken);
        if (page?.NotionId is null) return new(false, "Only pages imported from Notion can be pushed.", 0, 0, 0);
        var (token, unreadable) = UnprotectToken(settings.IntegrationToken);
        if (unreadable || string.IsNullOrWhiteSpace(token)) return new(false, "The Notion token is unavailable. Reconnect first.", 0, 0, 0);

        try
        {
            var remote = await notionService.GetPageAsync(token, page.NotionId, cancellationToken);
            if (remote is null) return new(false, "The Notion page could not be retrieved.", 0, 0, 0);
            if (settings.LastSyncedAt is { } lastSync && remote.Value.TryGetProperty("last_edited_time", out var edited)
                && DateTimeOffset.TryParse(edited.GetString(), out var remoteEditedAt) && remoteEditedAt > lastSync)
                return new(false, "Notion changed after the last import. Sync first to avoid overwriting remote work.", 0, 0, 0);

            var titleProperty = remote.Value.GetProperty("properties").EnumerateObject()
                .FirstOrDefault(property => property.Value.TryGetProperty("type", out var type) && type.GetString() == "title");
            if (!string.IsNullOrWhiteSpace(titleProperty.Name))
            {
                var titlePayload = new Dictionary<string, object?>
                {
                    ["properties"] = new Dictionary<string, object?>
                    {
                        [titleProperty.Name] = new { title = new[] { new { type = "text", text = new { content = page.Title } } } }
                    }
                };
                await notionService.UpdatePageAsync(token, page.NotionId, titlePayload, cancellationToken);
            }
            await notionService.ReplaceBlockChildrenAsync(token, page.NotionId, NotionMapping.MapBlocksForWrite(WikiBlockJson.ParseBlocks(page.BlocksJson)), cancellationToken);
            return new(true, "Page pushed to Notion.", 0, 1, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unable to push Sentinel page {WikiPageId} to Notion", wikiPageId);
            return new(false, $"Push failed: {ex.Message}", 0, 0, 0);
        }
    }

    private static IReadOnlyList<string> ParseSelectedIds(string value) => value
        .Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static IReadOnlyList<string> DeserializeSelectedIds(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
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

    private sealed record PageContentSyncResult(
        IReadOnlyList<WikiBlock> Blocks,
        bool UsedMarkdownFallback,
        bool ContentUnavailable)
    {
        public int BlockCount => Blocks.Count;
    }

    private sealed record DatabaseContentSyncResult(
        int Imported,
        int Updated,
        int Archived,
        int ContentBlocks,
        int MarkdownFallbackPages,
        int EmptyContentPages);
}
