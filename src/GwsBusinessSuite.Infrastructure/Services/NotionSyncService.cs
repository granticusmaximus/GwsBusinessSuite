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
    private const string CurrentImportMappingVersion = "3";

    public async Task<NotionConnectorSettingsView?> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.NotionConnectorSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var (_, isUnreadable) = UnprotectToken(row.IntegrationToken);
        var (webhookToken, webhookTokenUnreadable) = UnprotectToken(row.WebhookVerificationToken);
        return new NotionConnectorSettingsView
        {
            // Never return the decrypted credential to the Blazor client. A blank input means
            // "keep the stored token"; entering a value explicitly replaces it.
            IntegrationToken = string.Empty,
            HasStoredIntegrationToken = !string.IsNullOrWhiteSpace(row.IntegrationToken),
            AuthenticationMode = row.AuthenticationMode,
            IsOAuthConnected = string.Equals(row.AuthenticationMode, "oauth", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(row.IntegrationToken),
            WorkspaceId = row.WorkspaceId,
            WorkspaceIconUrl = row.WorkspaceIconUrl,
            WorkspaceName = row.WorkspaceName,
            AutoSyncEnabled = row.AutoSyncEnabled,
            SyncDirection = row.SyncDirection,
            SelectedNotionIds = string.Join(", ", DeserializeSelectedIds(row.SelectedNotionIdsJson)),
            AllowTwoWayWrites = row.AllowTwoWayWrites,
            LastSyncedAt = row.LastSyncedAt,
            LastSyncImportedCount = row.LastSyncImportedCount,
            LastSyncUpdatedCount = row.LastSyncUpdatedCount,
            LastSyncArchivedCount = row.LastSyncArchivedCount,
            LastSyncDiscoveredCount = row.LastSyncDiscoveredCount,
            LastSyncSkippedCount = row.LastSyncSkippedCount,
            LastSyncEmptyContentCount = row.LastSyncEmptyContentCount,
            LastSyncContentBlockCount = row.LastSyncContentBlockCount,
            WebhookVerificationToken = webhookTokenUnreadable ? string.Empty : webhookToken,
            HasWebhookVerificationToken = !string.IsNullOrWhiteSpace(row.WebhookVerificationToken)
                && !webhookTokenUnreadable,
            WebhookVerificationReceivedAt = row.WebhookVerificationReceivedAt,
            LastWebhookReceivedAt = row.LastWebhookReceivedAt,
            LastWebhookEventType = row.LastWebhookEventType,
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
            row.OAuthRefreshToken = string.Empty;
            row.AuthenticationMode = "internal";
            row.OAuthBotId = null;
            row.WorkspaceId = null;
            row.WorkspaceIconUrl = null;
            row.OAuthConnectedAt = null;
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

    public async Task<NotionPickerResult> BrowseAsync(
        string integrationToken,
        CancellationToken cancellationToken = default)
    {
        var token = integrationToken.Trim();
        if (token.Length == 0)
        {
            var settings = await dbContext.NotionConnectorSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
            if (settings is null || string.IsNullOrWhiteSpace(settings.IntegrationToken))
            {
                return new NotionPickerResult(
                    false,
                    "Enter an integration token or save a Notion connection before browsing.",
                    []);
            }

            var (storedToken, isUnreadable) = UnprotectToken(settings.IntegrationToken);
            if (isUnreadable)
            {
                return new NotionPickerResult(
                    false,
                    "The stored Notion token can no longer be decrypted. Enter a replacement token.",
                    []);
            }
            token = storedToken;
        }

        try
        {
            var discovered = new List<JsonElement>();
            string? cursor = null;
            do
            {
                var page = await notionService.SearchAsync(token, cursor, cancellationToken);
                discovered.AddRange(page.Results);
                cursor = page.HasMore ? page.NextCursor : null;
            } while (cursor is not null);

            var items = discovered
                .Where(item =>
                {
                    var objectType = item.TryGetProperty("object", out var objectElement)
                        ? objectElement.GetString()
                        : null;
                    return GetNotionId(item) is not null && !IsDatabaseRow(item, objectType);
                })
                .Select(item =>
                {
                    var objectType = item.TryGetProperty("object", out var objectElement)
                        ? objectElement.GetString() ?? "page"
                        : "page";
                    return new NotionPickerItem(
                        GetNotionId(item)!,
                        ExtractTitle(item, objectType),
                        objectType,
                        GetParentNotionId(item));
                })
                .DistinctBy(item => NormalizeNotionId(item.Id), StringComparer.OrdinalIgnoreCase)
                .ToList();

            return items.Count == 0
                ? new NotionPickerResult(
                    false,
                    "No shared pages or databases are discoverable. Share content with the integration in Notion, then browse again.",
                    [])
                : new NotionPickerResult(
                    true,
                    $"Found {items.Count} shared page{(items.Count == 1 ? string.Empty : "s")} and database{(items.Count == 1 ? string.Empty : "s")}.",
                    items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Unable to browse Notion content.");
            return new NotionPickerResult(false, $"Unable to browse Notion content. {ex.Message}", []);
        }
    }

    public async Task ResetWebhookVerificationAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.NotionConnectorSettings.FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Connect a Notion workspace before configuring a webhook.");
        settings.WebhookVerificationToken = string.Empty;
        settings.WebhookVerificationReceivedAt = null;
        settings.LastWebhookReceivedAt = null;
        settings.LastWebhookEventType = null;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        settings.UpdatedBy = "user";
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<NotionSyncResult> SyncAsync(CancellationToken cancellationToken = default) =>
        SyncAsync(forceRefresh: false, cancellationToken);

    public async Task<NotionSyncResult> SyncAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default)
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
            var skippedUnchangedPages = 0;
            var skippedUnchangedDatabaseRows = 0;
            var previousSuccessfulSyncAt = settingsRow.LastSyncedAt;

            // 1. Flat discovery pass - every page/database the integration can see.
            var discovered = new List<JsonElement>();
            string? searchCursor = null;
            do
            {
                var page = await notionService.SearchAsync(token, searchCursor, cancellationToken);
                discovered.AddRange(page.Results);
                searchCursor = page.HasMore ? page.NextCursor : null;
            } while (searchCursor is not null);

            var selectedNotionIds = DeserializeSelectedIds(settingsRow.SelectedNotionIdsJson);
            var selectedIds = selectedNotionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            var notionIdToRemoteEditedAt = new Dictionary<string, DateTimeOffset?>();
            var notionPageIdsNeedingContent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var notionDatabaseIdsNeedingSchema = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newNotionDatabaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var databaseContainerToLocalIds = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
            var existingPageSyncStates = (await dbContext.WikiPages
                    .AsNoTracking()
                    .Where(page => page.NotionId != null)
                    .Select(page => new { page.NotionId, page.NotionLastEditedAt, page.BlocksJson })
                    .ToListAsync(cancellationToken))
                .ToDictionary(
                    page => page.NotionId!,
                    page =>
                    {
                        var blocks = WikiBlockJson.ParseBlocks(page.BlocksJson);
                        return new ExistingPageSyncState(
                            page.NotionLastEditedAt,
                            blocks.Count > 0,
                            blocks.Any(block =>
                                block.Props.GetValueOrDefault("notionImportMappingVersion") == CurrentImportMappingVersion));
                    },
                    StringComparer.OrdinalIgnoreCase);
            var existingDatabaseWatermarks = (await dbContext.WikiDatabases
                    .AsNoTracking()
                    .Where(database => database.NotionId != null)
                    .Select(database => new { database.NotionId, database.NotionLastEditedAt })
                    .ToListAsync(cancellationToken))
                .ToDictionary(
                    database => database.NotionId!,
                    database => database.NotionLastEditedAt,
                    StringComparer.OrdinalIgnoreCase);
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
                if (objectType == "data_source"
                    && item.TryGetProperty("database_parent", out var databaseParentElement))
                {
                    // Since API version 2025-09-03 a data source's immediate parent is its
                    // database container. Its database_parent is where that database actually
                    // appears in Notion's page tree.
                    notionIdToParent[notionId] = databaseParentElement.Clone();
                }
                else if (item.TryGetProperty("parent", out var parentElement))
                {
                    notionIdToParent[notionId] = parentElement.Clone();
                }

                var isArchived = IsArchived(item);
                var title = ExtractTitle(item, objectType);
                var remoteEditedAt = GetLastEditedAt(item);
                notionIdToRemoteEditedAt[notionId] = remoteEditedAt;

                Guid localId;
                bool wasNew;
                bool becameArchived;
                if (objectType is "database" or "data_source")
                {
                    (localId, wasNew, becameArchived) = await UpsertDatabaseAsync(notionId, title, isArchived, cancellationToken);
                    existingDatabaseWatermarks.TryGetValue(notionId, out var priorWatermark);
                    if (forceRefresh
                        || ShouldRefreshRemoteContent(wasNew, remoteEditedAt, priorWatermark, previousSuccessfulSyncAt))
                    {
                        notionDatabaseIdsNeedingSchema.Add(notionId);
                    }
                    else if (priorWatermark is null && remoteEditedAt is not null)
                    {
                        dbContext.WikiDatabases.Local.Single(database => database.Id == localId)
                            .NotionLastEditedAt = remoteEditedAt;
                    }
                    if (wasNew)
                    {
                        newNotionDatabaseIds.Add(notionId);
                    }
                }
                else
                {
                    (localId, wasNew, becameArchived) = await UpsertPageAsync(
                        notionId,
                        title,
                        item,
                        isArchived,
                        remoteEditedAt,
                        reservedPageSlugs,
                        cancellationToken);
                    existingPageSyncStates.TryGetValue(notionId, out var priorSyncState);
                    var priorWatermark = priorSyncState?.LastEditedAt;
                    if (forceRefresh
                        || !(priorSyncState?.HasCurrentMappingVersion ?? false)
                        || ShouldRefreshRemoteContent(
                            wasNew,
                            remoteEditedAt,
                            priorWatermark,
                            previousSuccessfulSyncAt,
                            priorSyncState?.HasImportedContent ?? false))
                    {
                        notionPageIdsNeedingContent.Add(notionId);
                    }
                    else if (priorWatermark is null && remoteEditedAt is not null)
                    {
                        dbContext.WikiPages.Local.Single(page => page.Id == localId)
                            .NotionLastEditedAt = remoteEditedAt;
                    }
                }

                notionIdToLocalId[notionId] = localId;
                if (objectType == "data_source"
                    && item.TryGetProperty("parent", out var dataSourceParent)
                    && dataSourceParent.TryGetProperty("type", out var dataSourceParentType)
                    && dataSourceParentType.GetString() == "database_id"
                    && dataSourceParent.TryGetProperty("database_id", out var databaseIdElement)
                    && databaseIdElement.GetString() is { Length: > 0 } databaseId)
                {
                    var containerId = NormalizeNotionId(databaseId);
                    if (!databaseContainerToLocalIds.TryGetValue(containerId, out var localIds))
                    {
                        localIds = [];
                        databaseContainerToLocalIds[containerId] = localIds;
                    }
                    localIds.Add(localId);
                }
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

            // 4. Per-page block sync. The first-level child_page/child_database blocks are
            // also the authoritative sibling order for the Notion page tree.
            var childOrderByParentLocalId = new Dictionary<Guid, IReadOnlyList<NotionTreeChild>>();
            foreach (var (notionId, localId) in notionIdToLocalId)
            {
                if (notionIdToKind[notionId] is not ("database" or "data_source"))
                {
                    if (!notionPageIdsNeedingContent.Contains(notionId))
                    {
                        skippedUnchangedPages++;
                        continue;
                    }

                    var pageContent = await SyncPageBlocksAsync(
                        notionId,
                        localId,
                        token,
                        notionIdToRemoteEditedAt[notionId],
                        notionIdToLocalId,
                        databaseContainerToLocalIds,
                        cancellationToken);
                    childOrderByParentLocalId[localId] = pageContent.TreeChildren;
                    contentBlocks += pageContent.BlockCount;
                    if (pageContent.UsedMarkdownFallback) markdownFallbackPages++;
                    if (pageContent.BlockCount == 0) emptyContentPages++;
                    await SyncPageCommentsAsync(notionId, localId, token, cancellationToken);
                }
            }

            await ReconcileTreeOrderAsync(
                discovered,
                selectedNotionIds,
                notionIdToLocalId,
                databaseContainerToLocalIds,
                childOrderByParentLocalId,
                cancellationToken);

            // 5. Per-database property/row sync.
            foreach (var (notionId, localId) in notionIdToLocalId)
            {
                if (notionIdToKind[notionId] is "database" or "data_source")
                {
                    var notionDatabaseContainerId = databaseContainerToLocalIds
                        .FirstOrDefault(pair => pair.Value.Contains(localId))
                        .Key;
                    if (string.IsNullOrWhiteSpace(notionDatabaseContainerId)
                        && notionIdToKind[notionId] == "database")
                    {
                        notionDatabaseContainerId = notionId;
                    }
                    var databaseContent = await SyncDatabaseSchemaAndRowsAsync(
                        notionId,
                        notionDatabaseContainerId,
                        localId,
                        token,
                        notionDatabaseIdsNeedingSchema.Contains(notionId),
                        notionIdToRemoteEditedAt[notionId],
                        forceRefresh || newNotionDatabaseIds.Contains(notionId)
                            ? null
                            : previousSuccessfulSyncAt,
                        forceRefresh,
                        cancellationToken);
                    imported += databaseContent.Imported;
                    updated += databaseContent.Updated;
                    archived += databaseContent.Archived;
                    contentBlocks += databaseContent.ContentBlocks;
                    markdownFallbackPages += databaseContent.MarkdownFallbackPages;
                    emptyContentPages += databaseContent.EmptyContentPages;
                    skippedUnchangedDatabaseRows += databaseContent.SkippedRows;
                }
            }

            // 6. Archival - anything from a previous sync no longer returned by this pass.
            if (selectedIds.Count == 0)
            {
                archived += await ArchiveMissingAsync(seenTopLevelNotionIds, cancellationToken);
            }

            // 7. Resolve relation row references. NotionMapping.ApplyPropertyValue writes raw
            // Notion page ids for a Relation property (it's a pure JSON mapper with no DB
            // access to look up the local row a Notion id became); now that every database in
            // this pass has been upserted, rewrite whatever became resolvable to local
            // WikiDatabaseRow ids. Must run after every database sync, not per-database,
            // since a relation's target row can live in a database processed later in the
            // same pass.
            await ResolveRelationRowIdsAsync(cancellationToken);

            settingsRow.LastSyncedAt = DateTimeOffset.UtcNow;
            settingsRow.LastSyncImportedCount = imported;
            settingsRow.LastSyncUpdatedCount = updated;
            settingsRow.LastSyncArchivedCount = archived;
            settingsRow.LastSyncDiscoveredCount = discovered.Count;
            settingsRow.LastSyncSkippedCount = skippedUnchangedPages + skippedUnchangedDatabaseRows;
            settingsRow.LastSyncEmptyContentCount = emptyContentPages;
            settingsRow.LastSyncContentBlockCount = contentBlocks;
            settingsRow.UpdatedAt = DateTimeOffset.UtcNow;
            settingsRow.UpdatedBy = "notion-sync";
            await dbContext.SaveChangesAsync(cancellationToken);

            var message = forceRefresh
                ? $"Full sync complete. Refreshed {contentBlocks} content block{(contentBlocks == 1 ? string.Empty : "s")}."
                : $"Sync complete. Imported {contentBlocks} content block{(contentBlocks == 1 ? string.Empty : "s")}.";
            if (markdownFallbackPages > 0)
            {
                message += $" Recovered {markdownFallbackPages} page{(markdownFallbackPages == 1 ? string.Empty : "s")} through Notion's full-page content endpoint.";
            }
            if (emptyContentPages > 0)
            {
                message += $" {emptyContentPages} page{(emptyContentPages == 1 ? string.Empty : "s")} returned no readable content; confirm the integration has Read content capability and access to those pages.";
            }
            if (skippedUnchangedPages > 0)
            {
                message += $" Skipped {skippedUnchangedPages} unchanged page{(skippedUnchangedPages == 1 ? string.Empty : "s")}.";
            }
            if (skippedUnchangedDatabaseRows > 0)
            {
                message += $" Skipped {skippedUnchangedDatabaseRows} unchanged database row{(skippedUnchangedDatabaseRows == 1 ? string.Empty : "s")}.";
            }

            return new NotionSyncResult(
                true,
                message,
                imported,
                updated,
                archived,
                discovered.Count,
                skippedUnchangedPages + skippedUnchangedDatabaseRows,
                emptyContentPages,
                contentBlocks);
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

    private static DateTimeOffset? GetLastEditedAt(JsonElement item) =>
        item.TryGetProperty("last_edited_time", out var editedElement)
        && editedElement.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(editedElement.GetString(), out var editedAt)
            ? editedAt
            : null;

    private static bool ShouldRefreshRemoteContent(
        bool isNew,
        DateTimeOffset? remoteEditedAt,
        DateTimeOffset? importedRemoteEditedAt,
        DateTimeOffset? previousSuccessfulSyncAt,
        bool hasImportedContent = true)
    {
        if (isNew || remoteEditedAt is null)
        {
            return true;
        }

        if (importedRemoteEditedAt is { } itemWatermark)
        {
            return remoteEditedAt > itemWatermark;
        }

        // The connector's global LastSyncedAt only proves that discovery completed. Older
        // releases could create the page shell while its block import failed, so an empty
        // page with no item-level watermark must be fetched instead of being bootstrapped
        // as current and skipped forever.
        if (!hasImportedContent)
        {
            return true;
        }

        // Bootstrap existing installations when this column is first deployed. A successful
        // connector sync plus persisted content proves data at or before its global watermark
        // was imported.
        return previousSuccessfulSyncAt is null || remoteEditedAt > previousSuccessfulSyncAt;
    }

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
        // A current Notion data_source is parented by an internal database container, while
        // database_parent identifies the page where users actually see that database.
        JsonElement parent;
        if (item.TryGetProperty("object", out var objectType)
            && objectType.GetString() == "data_source"
            && item.TryGetProperty("database_parent", out var databaseParent))
        {
            parent = databaseParent;
        }
        else
        {
            parent = item.TryGetProperty("parent", out var directParent)
                ? directParent
                : default;
        }
        if (parent.ValueKind != JsonValueKind.Object
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

        if (parentNotionId is null)
        {
            return null;
        }

        if (notionIdToLocalId.TryGetValue(parentNotionId, out var parentLocalId))
        {
            return parentLocalId;
        }

        var normalizedParentId = NormalizeNotionId(parentNotionId);
        foreach (var (candidateNotionId, candidateLocalId) in notionIdToLocalId)
        {
            if (string.Equals(
                    NormalizeNotionId(candidateNotionId),
                    normalizedParentId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidateLocalId;
            }
        }

        return null;
    }

    private async Task ReconcileTreeOrderAsync(
        IReadOnlyList<JsonElement> discovered,
        IReadOnlyList<string> selectedNotionIds,
        IReadOnlyDictionary<string, Guid> notionIdToLocalId,
        IReadOnlyDictionary<string, List<Guid>> databaseContainerToLocalIds,
        IReadOnlyDictionary<Guid, IReadOnlyList<NotionTreeChild>> childOrderByParentLocalId,
        CancellationToken cancellationToken)
    {
        var pages = await dbContext.WikiPages.ToListAsync(cancellationToken);
        var databases = await dbContext.WikiDatabases.ToListAsync(cancellationToken);
        var nodes = pages
            .Select(page => new NotionTreeNodeState(
                page.Id,
                page.ParentWikiPageId,
                page.NotionId,
                page.SortOrder,
                value => page.SortOrder = value))
            .Concat(databases.Select(database => new NotionTreeNodeState(
                database.Id,
                database.ParentWikiPageId,
                database.NotionId,
                database.SortOrder,
                value => database.SortOrder = value)))
            .ToList();

        var localIdByNormalizedNotionId = notionIdToLocalId
            .GroupBy(pair => NormalizeNotionId(pair.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);

        void ReorderSiblings(Guid? parentId, IEnumerable<Guid> preferredIds)
        {
            var siblings = nodes
                .Where(node => node.ParentId == parentId)
                .ToDictionary(node => node.Id);
            if (siblings.Count == 0)
            {
                return;
            }

            var ordered = new List<NotionTreeNodeState>();
            foreach (var preferredId in preferredIds.Distinct())
            {
                if (siblings.Remove(preferredId, out var preferred))
                {
                    ordered.Add(preferred);
                }
            }

            // Preserve local-only pages and any remote child omitted by a partial Notion
            // response after the authoritative Notion-ordered items.
            ordered.AddRange(siblings.Values
                .OrderBy(node => node.SortOrder)
                .ThenBy(node => node.Id));

            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].SetSortOrder(index);
            }
        }

        var rootNotionIds = selectedNotionIds.Count > 0
            ? selectedNotionIds
            : discovered.Select(GetNotionId).Where(id => id is not null).Select(id => id!);
        var preferredRootIds = rootNotionIds
            .Select(NormalizeNotionId)
            .Where(localIdByNormalizedNotionId.ContainsKey)
            .Select(id => localIdByNormalizedNotionId[id]);
        ReorderSiblings(null, preferredRootIds);

        foreach (var (parentLocalId, remoteChildren) in childOrderByParentLocalId)
        {
            var preferredChildIds = new List<Guid>();
            foreach (var child in remoteChildren)
            {
                var normalizedChildId = NormalizeNotionId(child.NotionId);
                if (child.IsDatabase
                    && databaseContainerToLocalIds.TryGetValue(normalizedChildId, out var dataSourceLocalIds))
                {
                    preferredChildIds.AddRange(dataSourceLocalIds);
                    continue;
                }

                if (localIdByNormalizedNotionId.TryGetValue(normalizedChildId, out var localId))
                {
                    preferredChildIds.Add(localId);
                }
            }

            if (preferredChildIds.Count > 0)
            {
                ReorderSiblings(parentLocalId, preferredChildIds);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<(Guid LocalId, bool IsNew, bool BecameArchived)> UpsertPageAsync(
        string notionId,
        string title,
        JsonElement notionPage,
        bool isArchived,
        DateTimeOffset? remoteEditedAt,
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

        var titleChanged = !isNew && !string.Equals(page!.Title, title, StringComparison.Ordinal);
        var concurrentLocalEdit = !isNew
            && remoteEditedAt is { } remoteTimestamp
            && page!.NotionLastEditedAt is { } importedTimestamp
            && remoteTimestamp > importedTimestamp
            && !IsNotionActor(page.UpdatedBy);
        if (titleChanged && concurrentLocalEdit)
        {
            await UpsertPendingConflictAsync(
                page!,
                "title",
                JsonSerializer.Serialize(page!.Title),
                JsonSerializer.Serialize(title),
                remoteEditedAt!.Value,
                cancellationToken);
        }

        if (!titleChanged || !concurrentLocalEdit)
        {
            page!.Title = title;
        }
        var importedIcon = ExtractPageEmoji(notionPage);
        var cover = ExtractNotionFile(notionPage, "cover");
        var shouldRefreshPresentation = isNew
            || remoteEditedAt is null
            || page!.NotionLastEditedAt is null
            || remoteEditedAt > page.NotionLastEditedAt;
        if (shouldRefreshPresentation)
        {
            page!.Icon = importedIcon;
            page.CoverImageUrl = cover is null
                ? null
                : await PersistNotionPageAssetAsync(
                    notionId,
                    "cover",
                    cover.Value.Url,
                    cover.Value.IsTemporary,
                    cancellationToken);
        }
        page!.NotionArchivedAt = isArchived ? (page.NotionArchivedAt ?? DateTimeOffset.UtcNow) : null;
        if (!concurrentLocalEdit)
        {
            page.UpdatedAt = DateTimeOffset.UtcNow;
            page.UpdatedBy = "notion-sync";
        }
        if ((!titleChanged || !concurrentLocalEdit) && (titleChanged || wasArchived != isArchived))
        {
            page.ContentVersion++;
        }
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
        DateTimeOffset? remoteEditedAt,
        IReadOnlyDictionary<string, Guid> notionIdToLocalId,
        IReadOnlyDictionary<string, List<Guid>> databaseContainerToLocalIds,
        CancellationToken cancellationToken)
    {
        var pageContent = await LoadNotionPageBlocksAsync(
            notionPageId,
            token,
            cancellationToken,
            notionIdToLocalId,
            databaseContainerToLocalIds);
        pageContent = pageContent with { Blocks = StampCurrentMappingVersion(pageContent.Blocks) };

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
                var concurrentLocalEdit = remoteEditedAt is { } remoteTimestamp
                    && page.NotionLastEditedAt is { } importedTimestamp
                    && remoteTimestamp > importedTimestamp
                    && !IsNotionActor(page.UpdatedBy);
                if (concurrentLocalEdit)
                {
                    await UpsertPendingConflictAsync(
                        page,
                        "content",
                        page.BlocksJson,
                        blocksJson,
                        remoteEditedAt!.Value,
                        cancellationToken);
                }
                else
                {
                    page.BlocksJson = blocksJson;
                    page.ContentVersion++;
                }
            }
            page.NotionLastEditedAt = remoteEditedAt;
        }
        if (IsNotionActor(page.UpdatedBy)
            || !await dbContext.NotionSyncConflicts.AnyAsync(
                conflict => conflict.WikiPageId == page.Id && conflict.Status == "pending",
                cancellationToken))
        {
            page.UpdatedAt = DateTimeOffset.UtcNow;
            page.UpdatedBy = "notion-sync";
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return pageContent;
    }

    private async Task UpsertPendingConflictAsync(
        WikiPage page,
        string fieldName,
        string localValueJson,
        string remoteValueJson,
        DateTimeOffset remoteEditedAt,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.NotionSyncConflicts.FirstOrDefaultAsync(
            conflict => conflict.WikiPageId == page.Id
                && conflict.FieldName == fieldName
                && conflict.Status == "pending",
            cancellationToken);
        if (existing is null)
        {
            existing = new NotionSyncConflict
            {
                WikiPageId = page.Id,
                NotionId = page.NotionId!,
                FieldName = fieldName,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "notion-sync"
            };
            dbContext.NotionSyncConflicts.Add(existing);
        }

        existing.LocalValueJson = localValueJson;
        existing.RemoteValueJson = remoteValueJson;
        existing.RemoteEditedAt = remoteEditedAt;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        existing.UpdatedBy = "notion-sync";
    }

    private static bool IsNotionActor(string? actor) =>
        actor is "notion-sync" or "notion-push";

    private static string? ExtractPageEmoji(JsonElement page)
    {
        if (!page.TryGetProperty("icon", out var icon)
            || icon.ValueKind != JsonValueKind.Object
            || !icon.TryGetProperty("type", out var type)
            || type.GetString() != "emoji"
            || !icon.TryGetProperty("emoji", out var emoji)
            || emoji.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return emoji.GetString();
    }

    private static (string Url, bool IsTemporary)? ExtractNotionFile(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var holder)
            || holder.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = holder.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;
        foreach (var candidate in new[] { type, "file", "external", "file_upload" }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            if (holder.TryGetProperty(candidate!, out var source)
                && source.ValueKind == JsonValueKind.Object
                && source.TryGetProperty("url", out var urlElement)
                && urlElement.ValueKind == JsonValueKind.String
                && urlElement.GetString() is { Length: > 0 } url)
            {
                return (url, candidate is "file" or "file_upload");
            }
        }
        return null;
    }

    private async Task<string> PersistNotionPageAssetAsync(
        string notionPageId,
        string assetKind,
        string sourceUrl,
        bool isTemporary,
        CancellationToken cancellationToken)
    {
        if (!isTemporary)
        {
            return sourceUrl;
        }

        var assetKey = $"{assetKind}:{NormalizeNotionId(notionPageId)}";
        var importedFile = dbContext.SentinelImportedFiles.Local
            .FirstOrDefault(file => file.NotionBlockId == assetKey)
            ?? await dbContext.SentinelImportedFiles
                .FirstOrDefaultAsync(file => file.NotionBlockId == assetKey, cancellationToken);
        try
        {
            var download = await notionService.DownloadFileAsync(sourceUrl, cancellationToken);
            if (importedFile is null)
            {
                importedFile = new SentinelImportedFile
                {
                    NotionBlockId = assetKey,
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
            logger.LogWarning(
                ex,
                "Could not cache Notion {AssetKind} for page {NotionPageId}; retaining any existing durable copy.",
                assetKind,
                notionPageId);
            return importedFile is null
                ? sourceUrl
                : $"/admin/sentinel/files/{importedFile.Id}";
        }

        return $"/admin/sentinel/files/{importedFile.Id}";
    }

    private async Task<PageContentSyncResult> LoadNotionPageBlocksAsync(
        string notionPageId,
        string token,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, Guid>? notionIdToLocalId = null,
        IReadOnlyDictionary<string, List<Guid>>? databaseContainerToLocalIds = null)
    {
        var blocks = new List<WikiBlock>();
        var treeChildren = new List<NotionTreeChild>();
        var structuredContentUnavailable = false;
        try
        {
            await AppendBlockChildrenAsync(
                notionPageId,
                0,
                token,
                blocks,
                cancellationToken,
                treeChildren,
                notionIdToLocalId,
                databaseContainerToLocalIds);
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
            return new PageContentSyncResult(blocks, treeChildren, false, false);
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
                return new PageContentSyncResult(blocks, treeChildren, false, structuredContentUnavailable);
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
            return new PageContentSyncResult(blocks, treeChildren, blocks.Count > 0, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not retrieve full-page Markdown fallback for Notion page {NotionPageId}.",
                notionPageId);
            return new PageContentSyncResult(blocks, treeChildren, false, structuredContentUnavailable);
        }
    }

    private async Task AppendBlockChildrenAsync(
        string notionBlockId,
        int indentLevel,
        string token,
        List<WikiBlock> blocks,
        CancellationToken cancellationToken,
        List<NotionTreeChild>? directTreeChildren = null,
        IReadOnlyDictionary<string, Guid>? notionIdToLocalId = null,
        IReadOnlyDictionary<string, List<Guid>>? databaseContainerToLocalIds = null)
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

            // column_list/column is special-cased the same way as table above. Each column is
            // retained as rich text in columnRichTextJson (including resolved child-page
            // wikilinks), with the original "|||" plain-text representation kept as a
            // backward-compatible search/history fallback.
            if (type == "column_list" && childId.Length > 0)
            {
                var columnChildren = new List<JsonElement>();
                string? columnListCursor = null;
                do
                {
                    var columnListPage = await notionService.GetBlockChildrenAsync(token, childId, columnListCursor, cancellationToken);
                    columnChildren.AddRange(columnListPage.Results);
                    columnListCursor = columnListPage.HasMore ? columnListPage.NextCursor : null;
                } while (columnListCursor is not null);

                var mappedColumns = new List<List<WikiBlock>>();
                foreach (var columnChild in columnChildren)
                {
                    var columnChildId = columnChild.TryGetProperty("id", out var columnIdElement) ? columnIdElement.GetString() ?? string.Empty : string.Empty;
                    var columnBlocks = new List<WikiBlock>();
                    if (columnChildId.Length > 0)
                    {
                        await AppendBlockChildrenAsync(
                            columnChildId,
                            0,
                            token,
                            columnBlocks,
                            cancellationToken,
                            directTreeChildren,
                            notionIdToLocalId,
                            databaseContainerToLocalIds);
                    }
                    mappedColumns.Add(columnBlocks);
                }

                if (mappedColumns.Count > 0)
                {
                    var columnTexts = mappedColumns
                        .Select(column => string.Join("\n", column.Select(block => block.PlainText)))
                        .ToList();
                    var columnRichText = mappedColumns.Select(JoinBlocksAsRichText).ToList();
                    var containsOnlyPageLinks = mappedColumns
                        .SelectMany(column => column)
                        .Any()
                        && mappedColumns
                            .SelectMany(column => column)
                            .All(block => block.Props.GetValueOrDefault("notionChildPage") == "true");

                    blocks.Add(new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Columns, indentLevel,
                        [new WikiRichTextSpan(string.Join("|||", columnTexts))],
                        new Dictionary<string, string>
                        {
                            ["columnRichTextJson"] = JsonSerializer.Serialize(columnRichText, WikiBlockJson.Options),
                            ["notionPageLinkColumns"] = containsOnlyPageLinks ? "true" : "false"
                        }));
                }
                continue;
            }

            if (NotionMapping.IsPageTreeBlock(type))
            {
                if (directTreeChildren is not null && childId.Length > 0)
                {
                    directTreeChildren.Add(new NotionTreeChild(childId, type == "child_database"));
                }
                blocks.AddRange(await MapPageTreeBlockAsync(
                    child,
                    type,
                    childId,
                    indentLevel,
                    notionIdToLocalId,
                    databaseContainerToLocalIds,
                    cancellationToken));
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
                    await AppendBlockChildrenAsync(
                        childId, indentLevel, token, blocks, cancellationToken, null,
                        notionIdToLocalId, databaseContainerToLocalIds);
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
                    await AppendBlockChildrenAsync(
                        childId, indentLevel, token, blocks, cancellationToken, null,
                        notionIdToLocalId, databaseContainerToLocalIds);
                }
                continue;
            }

            mapped = await PersistNotionFileAsync(mapped, cancellationToken);
            blocks.Add(mapped);
            if (hasChildren && childId.Length > 0)
            {
                await AppendBlockChildrenAsync(
                    childId, indentLevel + 1, token, blocks, cancellationToken, null,
                    notionIdToLocalId, databaseContainerToLocalIds);
            }
        }
    }

    private async Task<IReadOnlyList<WikiBlock>> MapPageTreeBlockAsync(
        JsonElement block,
        string type,
        string notionId,
        int indentLevel,
        IReadOnlyDictionary<string, Guid>? notionIdToLocalId,
        IReadOnlyDictionary<string, List<Guid>>? databaseContainerToLocalIds,
        CancellationToken cancellationToken)
    {
        var title = block.TryGetProperty(type, out var body)
            && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("title", out var titleElement)
            && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString() ?? string.Empty
                : string.Empty;

        if (type == "child_page")
        {
            var localId = ResolveLocalId(notionId, notionIdToLocalId);
            var page = localId is null
                ? null
                : await dbContext.WikiPages.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.Id == localId.Value, cancellationToken);
            var displayTitle = string.IsNullOrWhiteSpace(title) ? page?.Title ?? "Untitled" : title;
            var icon = string.IsNullOrWhiteSpace(page?.Icon) ? "📄" : page.Icon;
            var link = page is null ? null : $"wikilink:{page.Id}";
            return
            [
                new WikiBlock(
                    Guid.NewGuid(),
                    WikiBlockTypes.Paragraph,
                    indentLevel,
                    [
                        new WikiRichTextSpan($"{icon} {displayTitle}", Link: link)
                    ],
                    new Dictionary<string, string> { ["notionChildPage"] = "true" })
            ];
        }

        var databaseIds = new List<Guid>();
        if (databaseContainerToLocalIds is not null)
        {
            var normalizedId = NormalizeNotionId(notionId);
            var match = databaseContainerToLocalIds.FirstOrDefault(
                pair => NormalizeNotionId(pair.Key) == normalizedId);
            if (match.Value is not null)
            {
                databaseIds.AddRange(match.Value);
            }
        }
        var directlyResolvedId = ResolveLocalId(notionId, notionIdToLocalId);
        if (databaseIds.Count == 0 && directlyResolvedId is not null)
        {
            databaseIds.Add(directlyResolvedId.Value);
        }

        var mapped = new List<WikiBlock>();
        foreach (var databaseId in databaseIds.Distinct())
        {
            var database = await dbContext.WikiDatabases.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == databaseId, cancellationToken);
            if (database is null)
            {
                continue;
            }
            mapped.Add(new WikiBlock(
                Guid.NewGuid(),
                WikiBlockTypes.InlineDatabase,
                indentLevel,
                [],
                new Dictionary<string, string>
                {
                    ["databaseId"] = database.Id.ToString(),
                    ["databaseTitle"] = string.IsNullOrWhiteSpace(title) ? database.Title : title,
                    ["databaseIcon"] = string.IsNullOrWhiteSpace(database.Icon) ? "▦" : database.Icon
                }));
        }

        if (mapped.Count > 0)
        {
            return mapped;
        }

        return
        [
            new WikiBlock(
                Guid.NewGuid(),
                WikiBlockTypes.Paragraph,
                indentLevel,
                [new WikiRichTextSpan($"▦ {(string.IsNullOrWhiteSpace(title) ? "Database" : title)}")],
                new Dictionary<string, string> { ["notionChildDatabase"] = "true" })
        ];
    }

    private static Guid? ResolveLocalId(
        string notionId,
        IReadOnlyDictionary<string, Guid>? notionIdToLocalId)
    {
        if (notionIdToLocalId is null)
        {
            return null;
        }
        if (notionIdToLocalId.TryGetValue(notionId, out var exact))
        {
            return exact;
        }
        var normalizedId = NormalizeNotionId(notionId);
        foreach (var pair in notionIdToLocalId)
        {
            if (NormalizeNotionId(pair.Key) == normalizedId)
            {
                return pair.Value;
            }
        }
        return null;
    }

    private static IReadOnlyList<WikiRichTextSpan> JoinBlocksAsRichText(List<WikiBlock> blocks)
    {
        var spans = new List<WikiRichTextSpan>();
        foreach (var block in blocks)
        {
            if (spans.Count > 0)
            {
                spans.Add(new WikiRichTextSpan("\n"));
            }
            spans.AddRange(block.RichText);
        }
        return spans;
    }

    private static IReadOnlyList<WikiBlock> StampCurrentMappingVersion(IReadOnlyList<WikiBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return blocks;
        }
        var stamped = blocks.ToList();
        var props = stamped[0].Props.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        props["notionImportMappingVersion"] = CurrentImportMappingVersion;
        stamped[0] = stamped[0] with { Props = props };
        return stamped;
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
        string? notionDatabaseContainerId,
        Guid wikiDatabaseId,
        string token,
        bool syncSchema,
        DateTimeOffset? remoteEditedAt,
        DateTimeOffset? rowsEditedAfter,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var notionPropertyIdToLocal = new Dictionary<string, (Guid Id, string Type)>();
        JsonElement? schemaForViews = null;
        if (syncSchema)
        {
            var schema = await notionService.GetDatabaseAsync(token, notionDatabaseId, cancellationToken);
            if (schema is null)
            {
                return new DatabaseContentSyncResult(0, 0, 0, 0, 0, 0, 0);
            }
            schemaForViews = schema.Value;

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
                    else if (localType == WikiDatabasePropertyTypes.Relation)
                    {
                        // Preserve an already-resolved RelatedDatabaseId even if this run's
                        // target lookup comes back empty (e.g. a transient miss, or the
                        // property temporarily has no target in this schema payload) - only
                        // ever move from unresolved to resolved, never resolved back to null.
                        var existingConfig = WikiDatabasePropertyConfig.Parse(property.ConfigJson);
                        var relatedDatabaseId = existingConfig.RelatedDatabaseId;
                        var targetNotionDatabaseId = NotionMapping.ExtractRelationTargetNotionId(propertySchema);
                        if (targetNotionDatabaseId is not null)
                        {
                            var resolvedId = await dbContext.WikiDatabases.AsNoTracking()
                                .Where(database => database.NotionId == targetNotionDatabaseId)
                                .Select(database => (Guid?)database.Id)
                                .FirstOrDefaultAsync(cancellationToken);
                            if (resolvedId is not null)
                            {
                                relatedDatabaseId = resolvedId;
                            }
                        }
                        property.ConfigJson = WikiDatabasePropertyConfig.Serialize(existingConfig with { RelatedDatabaseId = relatedDatabaseId });
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
        }
        else
        {
            notionPropertyIdToLocal = (await dbContext.WikiDatabaseProperties
                    .AsNoTracking()
                    .Where(property => property.WikiDatabaseId == wikiDatabaseId && property.NotionId != null)
                    .Select(property => new { property.NotionId, property.Id, property.Type })
                    .ToListAsync(cancellationToken))
                .ToDictionary(
                    property => property.NotionId!,
                    property => (property.Id, property.Type),
                    StringComparer.Ordinal);
        }
        await SyncDatabaseViewsAsync(
            schemaForViews,
            notionDatabaseContainerId,
            notionDatabaseId,
            wikiDatabaseId,
            token,
            notionPropertyIdToLocal,
            cancellationToken);

        var rows = new List<JsonElement>();
        string? rowCursor = null;
        do
        {
            var page = await notionService.QueryDatabaseAsync(
                token,
                notionDatabaseId,
                rowCursor,
                rowsEditedAfter,
                cancellationToken);
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
        var skippedRows = 0;
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
            var rowRemoteEditedAt = GetLastEditedAt(rowElement);
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
            if (forceRefresh
                || ShouldRefreshRemoteContent(
                    isNew,
                    rowRemoteEditedAt,
                    row.NotionLastEditedAt,
                    rowsEditedAfter))
            {
                var rowContent = await LoadNotionPageBlocksAsync(rowNotionId, token, cancellationToken);
                if (!rowContent.ContentUnavailable)
                {
                    row.BlocksJson = WikiBlockJson.Serialize(rowContent.Blocks);
                    row.NotionLastEditedAt = rowRemoteEditedAt;
                }
                contentBlocks += rowContent.BlockCount;
                if (rowContent.UsedMarkdownFallback) markdownFallbackPages++;
                if (rowContent.BlockCount == 0) emptyContentPages++;
            }
            else
            {
                skippedRows++;
            }
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

        if (rowsEditedAfter is null)
        {
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
        }

        if (syncSchema)
        {
            var database = await dbContext.WikiDatabases.FirstAsync(
                item => item.Id == wikiDatabaseId,
                cancellationToken);
            database.NotionLastEditedAt = remoteEditedAt;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new DatabaseContentSyncResult(
            imported,
            updated,
            archived,
            contentBlocks,
            markdownFallbackPages,
            emptyContentPages,
            skippedRows);
    }

    private async Task SyncDatabaseViewsAsync(
        JsonElement? schema,
        string? notionDatabaseContainerId,
        string notionDataSourceId,
        Guid wikiDatabaseId,
        string token,
        IReadOnlyDictionary<string, (Guid Id, string Type)> notionPropertyIdToLocal,
        CancellationToken cancellationToken)
    {
        var viewReferences = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await notionService.ListViewsAsync(
                token,
                notionDatabaseContainerId,
                notionDataSourceId,
                cursor,
                cancellationToken);
            viewReferences.AddRange(page.Results);
            cursor = page.HasMore ? page.NextCursor : null;
        } while (cursor is not null);

        // Compatibility fallback for responses and test fixtures created before Notion
        // moved view discovery to GET /v1/views.
        if (viewReferences.Count == 0
            && schema is { } schemaElement
            && schemaElement.TryGetProperty("views", out var viewsElement)
            && viewsElement.ValueKind == JsonValueKind.Array)
        {
            viewReferences.AddRange(viewsElement.EnumerateArray().Select(view => view.Clone()));
        }

        var order = 0;
        foreach (var viewElement in viewReferences)
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
            var groupByPropertyId = ExtractNotionViewGroupPropertyId(remote);
            var localGroupByPropertyId = groupByPropertyId is not null
                && notionPropertyIdToLocal.TryGetValue(groupByPropertyId, out var localProperty)
                    ? localProperty.Id.ToString()
                    : null;
            existing.ConfigJson = WikiDatabaseViewConfigJson.Serialize(
                WikiDatabaseViewConfig.Empty with { GroupByPropertyId = localGroupByPropertyId });
            existing.SortOrder = order++;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedBy = "notion-sync";
            if (isNew) dbContext.WikiDatabaseViews.Add(existing);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string? ExtractNotionViewGroupPropertyId(JsonElement view)
    {
        if (!view.TryGetProperty("configuration", out var configuration)
            || configuration.ValueKind != JsonValueKind.Object
            || !configuration.TryGetProperty("group_by", out var groupBy)
            || groupBy.ValueKind != JsonValueKind.Object
            || !groupBy.TryGetProperty("property_id", out var propertyId)
            || propertyId.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return propertyId.GetString();
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

    public async Task<NotionSyncResult> PushDatabaseRowAsync(
        Guid wikiDatabaseRowId,
        CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.NotionConnectorSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null || settings.SyncDirection != "twoWay" || !settings.AllowTwoWayWrites)
        {
            return new(false, "Two-way sync and the write acknowledgement must both be enabled.", 0, 0, 0);
        }

        var row = await dbContext.WikiDatabaseRows
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == wikiDatabaseRowId, cancellationToken);
        if (row?.NotionId is null)
        {
            return new(false, "Only database rows imported from Notion can be pushed.", 0, 0, 0);
        }

        var properties = await dbContext.WikiDatabaseProperties
            .AsNoTracking()
            .Where(property => property.WikiDatabaseId == row.WikiDatabaseId)
            .OrderBy(property => property.SortOrder)
            .ToListAsync(cancellationToken);
        var (token, unreadable) = UnprotectToken(settings.IntegrationToken);
        if (unreadable || string.IsNullOrWhiteSpace(token))
        {
            return new(false, "The Notion token is unavailable. Reconnect first.", 0, 0, 0);
        }

        try
        {
            var remote = await notionService.GetPageAsync(token, row.NotionId, cancellationToken);
            if (remote is null)
            {
                return new(false, "The Notion database row could not be retrieved.", 0, 0, 0);
            }

            if (row.NotionLastEditedAt is { } importedAt
                && GetLastEditedAt(remote.Value) is { } remoteEditedAt
                && remoteEditedAt > importedAt)
            {
                return new(
                    false,
                    "Notion changed this row after its last import. Sync and review the changes before pushing.",
                    0,
                    0,
                    0);
            }

            var relationIds = properties
                .Where(property => property.Type == WikiDatabasePropertyTypes.Relation)
                .SelectMany(property => WikiPropertyValues.GetMultiSelect(
                    WikiPropertyValues.ParseObject(row.PropertyValuesJson),
                    property.Id))
                .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();
            var relatedNotionIds = relationIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await dbContext.WikiDatabaseRows
                    .AsNoTracking()
                    .Where(item => relationIds.Contains(item.Id) && item.NotionId != null)
                    .ToDictionaryAsync(item => item.Id, item => item.NotionId!, cancellationToken);

            var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
            var propertyPayload = new Dictionary<string, object?>();
            foreach (var property in properties.Where(property => !string.IsNullOrWhiteSpace(property.NotionId)))
            {
                var mapped = MapDatabaseRowPropertyForWrite(property, values, relatedNotionIds);
                if (mapped.ShouldWrite)
                {
                    propertyPayload[property.NotionId!] = mapped.Value;
                }
            }

            var payload = new Dictionary<string, object?>
            {
                ["properties"] = propertyPayload
            };
            if (!string.IsNullOrWhiteSpace(row.Icon) && !row.Icon.Contains('/', StringComparison.Ordinal))
            {
                payload["icon"] = new { type = "emoji", emoji = row.Icon };
            }
            if (Uri.TryCreate(row.CoverImageUrl, UriKind.Absolute, out var coverUri)
                && coverUri.Scheme is "http" or "https")
            {
                payload["cover"] = new
                {
                    type = "external",
                    external = new { url = coverUri.ToString() }
                };
            }

            await notionService.UpdatePageAsync(token, row.NotionId, payload, cancellationToken);
            await notionService.ReplaceBlockChildrenAsync(
                token,
                row.NotionId,
                NotionMapping.MapBlocksForWrite(WikiBlockJson.ParseBlocks(row.BlocksJson)),
                cancellationToken);

            var refreshed = await notionService.GetPageAsync(token, row.NotionId, cancellationToken);
            var trackedRow = await dbContext.WikiDatabaseRows
                .FirstAsync(item => item.Id == row.Id, cancellationToken);
            trackedRow.NotionLastEditedAt = refreshed is { } refreshedPage
                ? GetLastEditedAt(refreshedPage)
                : DateTimeOffset.UtcNow;
            trackedRow.UpdatedAt = DateTimeOffset.UtcNow;
            trackedRow.UpdatedBy = "notion-push";
            await dbContext.SaveChangesAsync(cancellationToken);

            return new(true, $"Database row pushed to Notion ({propertyPayload.Count} properties and page content).", 0, 1, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unable to push Sentinel database row {WikiDatabaseRowId} to Notion", wikiDatabaseRowId);
            return new(false, $"Push failed: {ex.Message}", 0, 0, 0);
        }
    }

    private static (bool ShouldWrite, object? Value) MapDatabaseRowPropertyForWrite(
        WikiDatabaseProperty property,
        System.Text.Json.Nodes.JsonObject values,
        IReadOnlyDictionary<Guid, string> relatedNotionIds)
    {
        object RichText(string text) => new[]
        {
            new { type = "text", text = new { content = text } }
        };

        return property.Type switch
        {
            WikiDatabasePropertyTypes.Title =>
                (true, new { title = RichText(WikiPropertyValues.GetText(values, property.Id) ?? string.Empty) }),
            WikiDatabasePropertyTypes.Text =>
                (true, new { rich_text = RichText(WikiPropertyValues.GetText(values, property.Id) ?? string.Empty) }),
            WikiDatabasePropertyTypes.Url =>
                (true, new { url = WikiPropertyValues.GetText(values, property.Id) }),
            WikiDatabasePropertyTypes.Number =>
                (true, new { number = WikiPropertyValues.GetNumber(values, property.Id) }),
            WikiDatabasePropertyTypes.Checkbox =>
                (true, new { checkbox = WikiPropertyValues.GetCheckbox(values, property.Id) }),
            WikiDatabasePropertyTypes.Date =>
                (true, new
                {
                    date = WikiPropertyValues.GetDate(values, property.Id) is { } date
                        ? new { start = date.ToString("O") }
                        : null
                }),
            WikiDatabasePropertyTypes.Select =>
                (true, new
                {
                    select = WikiPropertyValues.GetText(values, property.Id) is { Length: > 0 } optionId
                        ? new { id = optionId }
                        : null
                }),
            WikiDatabasePropertyTypes.MultiSelect =>
                (true, new
                {
                    multi_select = WikiPropertyValues.GetMultiSelect(values, property.Id)
                        .Select(id => new { id })
                        .ToArray()
                }),
            WikiDatabasePropertyTypes.Relation =>
                (true, new
                {
                    relation = WikiPropertyValues.GetMultiSelect(values, property.Id)
                        .Select(value => Guid.TryParse(value, out var id)
                            && relatedNotionIds.TryGetValue(id, out var notionId)
                                ? notionId
                                : null)
                        .Where(notionId => notionId is not null)
                        .Select(notionId => new { id = notionId! })
                        .ToArray()
                }),
            // People values currently store display names, files store durable local copies,
            // and computed/audit properties are read-only. Writing them as if they were
            // Notion ids or upload ids would corrupt remote data, so omit them explicitly.
            _ => (false, null)
        };
    }

    public async Task<IReadOnlyList<NotionSyncConflictView>> GetPendingConflictsAsync(
        CancellationToken cancellationToken = default)
    {
        var conflicts = await dbContext.NotionSyncConflicts
            .AsNoTracking()
            .Where(conflict => conflict.Status == "pending")
            .Join(
                dbContext.WikiPages.AsNoTracking(),
                conflict => conflict.WikiPageId,
                page => page.Id,
                (conflict, page) => new NotionSyncConflictView(
                    conflict.Id,
                    conflict.WikiPageId,
                    page.Title,
                    conflict.FieldName,
                    conflict.LocalValueJson,
                    conflict.RemoteValueJson,
                    conflict.RemoteEditedAt,
                    conflict.CreatedAt))
            .ToListAsync(cancellationToken);
        return conflicts.OrderByDescending(conflict => conflict.DetectedAt).ToList();
    }

    public async Task ResolveConflictAsync(
        Guid conflictId,
        string resolution,
        string resolvedBy,
        CancellationToken cancellationToken = default)
    {
        if (resolution is not (NotionConflictResolutions.KeepSentinel or NotionConflictResolutions.UseNotion))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), "Unknown Notion conflict resolution.");
        }

        var conflict = await dbContext.NotionSyncConflicts
            .FirstOrDefaultAsync(item => item.Id == conflictId && item.Status == "pending", cancellationToken)
            ?? throw new InvalidOperationException("The Notion conflict no longer exists or was already resolved.");
        var page = await dbContext.WikiPages
            .FirstOrDefaultAsync(item => item.Id == conflict.WikiPageId, cancellationToken)
            ?? throw new InvalidOperationException("The conflicted Sentinel page no longer exists.");

        if (resolution == NotionConflictResolutions.UseNotion)
        {
            if (conflict.FieldName == "title")
            {
                page.Title = JsonSerializer.Deserialize<string>(conflict.RemoteValueJson) ?? page.Title;
            }
            else if (conflict.FieldName == "content")
            {
                _ = WikiBlockJson.ParseBlocks(conflict.RemoteValueJson);
                page.BlocksJson = conflict.RemoteValueJson;
            }
            else
            {
                throw new InvalidOperationException($"Unsupported conflict field '{conflict.FieldName}'.");
            }

            page.ContentVersion++;
            page.UpdatedAt = DateTimeOffset.UtcNow;
            page.UpdatedBy = resolvedBy;
        }

        conflict.Status = "resolved";
        conflict.Resolution = resolution;
        conflict.ResolvedAt = DateTimeOffset.UtcNow;
        conflict.ResolvedBy = resolvedBy;
        conflict.UpdatedAt = DateTimeOffset.UtcNow;
        conflict.UpdatedBy = resolvedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
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

    // Rewrites Relation property values from raw Notion page ids to local WikiDatabaseRow
    // ids wherever the target row now exists locally (see ApplyPropertyValue's Relation
    // case). Deliberately non-destructive for ids that still don't resolve - Notion itself
    // shows an inaccessible/not-yet-synced relation as simply not resolving rather than
    // dropping the link, and a later sync (once the target is imported) should still be able
    // to pick it up. Runs every sync, not just when something changed, since it's a pure
    // in-memory dictionary lookup with no further Notion API calls - cheap relative to the
    // rate-limited parts of a sync.
    private async Task ResolveRelationRowIdsAsync(CancellationToken cancellationToken)
    {
        var relationProperties = await dbContext.WikiDatabaseProperties
            .Where(property => property.Type == WikiDatabasePropertyTypes.Relation)
            .ToListAsync(cancellationToken);
        if (relationProperties.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var property in relationProperties)
        {
            var relatedDatabaseId = WikiDatabasePropertyConfig.Parse(property).RelatedDatabaseId;
            if (relatedDatabaseId is null)
            {
                continue;
            }

            var targetRowIdByNotionId = await dbContext.WikiDatabaseRows
                .Where(row => row.WikiDatabaseId == relatedDatabaseId && row.NotionId != null)
                .Select(row => new { row.NotionId, row.Id })
                .ToDictionaryAsync(row => row.NotionId!, row => row.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
            if (targetRowIdByNotionId.Count == 0)
            {
                continue;
            }

            var sourceRows = await dbContext.WikiDatabaseRows
                .Where(row => row.WikiDatabaseId == property.WikiDatabaseId)
                .ToListAsync(cancellationToken);
            foreach (var row in sourceRows)
            {
                var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                var currentIds = WikiPropertyValues.GetMultiSelect(values, property.Id);
                if (currentIds.Count == 0)
                {
                    continue;
                }

                var resolvedIds = currentIds
                    .Select(id => targetRowIdByNotionId.TryGetValue(id, out var localRowId) ? localRowId.ToString() : id)
                    .ToList();
                if (resolvedIds.SequenceEqual(currentIds, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                WikiPropertyValues.SetMultiSelect(values, property.Id, resolvedIds);
                row.PropertyValuesJson = WikiPropertyValues.Serialize(values);
                row.UpdatedAt = DateTimeOffset.UtcNow;
                row.UpdatedBy = "notion-sync";
                changed = true;
            }
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
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
        IReadOnlyList<NotionTreeChild> TreeChildren,
        bool UsedMarkdownFallback,
        bool ContentUnavailable)
    {
        public int BlockCount => Blocks.Count;
    }

    private sealed record NotionTreeChild(string NotionId, bool IsDatabase);
    private sealed record ExistingPageSyncState(
        DateTimeOffset? LastEditedAt,
        bool HasImportedContent,
        bool HasCurrentMappingVersion);

    private sealed record NotionTreeNodeState(
        Guid Id,
        Guid? ParentId,
        string? NotionId,
        int SortOrder,
        Action<int> SetSortOrder);

    private sealed record DatabaseContentSyncResult(
        int Imported,
        int Updated,
        int Archived,
        int ContentBlocks,
        int MarkdownFallbackPages,
        int EmptyContentPages,
        int SkippedRows);
}
