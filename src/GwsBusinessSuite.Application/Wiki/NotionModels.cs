using System.Text.Json;

namespace GwsBusinessSuite.Application.Wiki;

// One page of results from any of Notion's cursor-paginated endpoints (search, block
// children, database query) - they all share this exact shape (results/has_more/next_cursor),
// so a single record covers all three instead of one per endpoint.
public sealed record NotionPage(IReadOnlyList<JsonElement> Results, bool HasMore, string? NextCursor);

public sealed record NotionValidationResult(bool IsSuccess, string Message, string? WorkspaceName);

public sealed record NotionFileDownload(string FileName, string ContentType, byte[] Content);

// Raw HTTP client over Notion's public API - Bearer token passed per-call (never held as
// service state) since the token comes from a DB row the caller already loaded, same as
// CjConnectionRequest.DeveloperKey being passed into ICjAffiliateService per-call.
public interface INotionService
{
    Task<NotionValidationResult> ValidateConnectionAsync(string integrationToken, CancellationToken cancellationToken = default);
    Task<NotionPage> SearchAsync(string integrationToken, string? cursor, CancellationToken cancellationToken = default);
    Task<NotionPage> GetBlockChildrenAsync(string integrationToken, string blockId, string? cursor, CancellationToken cancellationToken = default);
    Task<JsonElement?> GetPageAsync(string integrationToken, string pageId, CancellationToken cancellationToken = default);
    Task<JsonElement?> GetDatabaseAsync(string integrationToken, string databaseId, CancellationToken cancellationToken = default);
    Task<NotionPage> QueryDatabaseAsync(string integrationToken, string databaseId, string? cursor, CancellationToken cancellationToken = default);
    Task<JsonElement?> GetViewAsync(string integrationToken, string viewId, CancellationToken cancellationToken = default);
    Task<NotionPage> ListCommentsAsync(string integrationToken, string blockId, string? cursor, CancellationToken cancellationToken = default);
    Task<NotionFileDownload> DownloadFileAsync(string fileUrl, CancellationToken cancellationToken = default);
    Task UpdatePageAsync(string integrationToken, string pageId, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken = default);
    Task ReplaceBlockChildrenAsync(string integrationToken, string blockId, IReadOnlyList<object> children, CancellationToken cancellationToken = default);
}

public sealed class NotionConnectorSettingsView
{
    public string IntegrationToken { get; set; } = string.Empty;
    public bool HasStoredIntegrationToken { get; set; }
    public string? WorkspaceName { get; set; }
    public bool AutoSyncEnabled { get; set; } = true;
    public string SyncDirection { get; set; } = "import";
    public string SelectedNotionIds { get; set; } = string.Empty;
    public bool AllowTwoWayWrites { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public int LastSyncImportedCount { get; set; }
    public int LastSyncUpdatedCount { get; set; }
    public int LastSyncArchivedCount { get; set; }

    // True when the stored token's ciphertext could not be decrypted (Data Protection key
    // ring rotated since it was saved) - mirrors CjConnectorSettingsView's
    // DeveloperKeyUnreadable so the UI can prompt for re-entry instead of silently failing.
    public bool IntegrationTokenUnreadable { get; set; }
}

public sealed record NotionSyncResult(bool IsSuccess, string Message, int Imported, int Updated, int Archived);

public static class NotionSyncJobStates
{
    public const string Idle = "idle";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
}

public sealed record NotionSyncJobStatus(
    string State,
    string Source,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    NotionSyncResult? Result)
{
    public bool IsActive => State is NotionSyncJobStates.Queued or NotionSyncJobStates.Running;

    public static NotionSyncJobStatus Idle { get; } =
        new(NotionSyncJobStates.Idle, "none", null, null, null);
}

// Manual sync is dispatched through the hosted worker rather than awaited on a Blazor
// circuit. The import therefore survives a browser reconnect, navigation, or mobile app
// suspension, while this status snapshot lets the UI observe the server-owned run.
public interface INotionSyncCoordinator
{
    bool TryQueueManualSync();
    NotionSyncJobStatus GetStatus();
}

// Reconciliation (search -> upsert pages/databases -> wire hierarchy -> sync blocks/rows) and
// connector-settings CRUD, mirroring how ICjAdsService bundles both connector settings and
// sync operations behind one interface rather than splitting settings into their own service.
public interface INotionSyncService
{
    Task<NotionConnectorSettingsView?> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<NotionValidationResult> SaveSettingsAsync(NotionConnectorSettingsView settings, CancellationToken cancellationToken = default);
    Task<NotionSyncResult> SyncAsync(CancellationToken cancellationToken = default);
    Task<NotionSyncResult> PushPageAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
}
