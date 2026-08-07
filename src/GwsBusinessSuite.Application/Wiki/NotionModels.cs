using System.Text.Json;

namespace GwsBusinessSuite.Application.Wiki;

// One page of results from any of Notion's cursor-paginated endpoints (search, block
// children, database query) - they all share this exact shape (results/has_more/next_cursor),
// so a single record covers all three instead of one per endpoint.
public sealed record NotionPage(IReadOnlyList<JsonElement> Results, bool HasMore, string? NextCursor);

public sealed record NotionValidationResult(bool IsSuccess, string Message, string? WorkspaceName);

public sealed class NotionOAuthOptions
{
    public const string SectionName = "NotionOAuth";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && Uri.TryCreate(RedirectUri, UriKind.Absolute, out _);
}

public sealed record NotionOAuthConnectionResult(
    bool IsSuccess,
    string Message,
    string? WorkspaceName = null);

public interface INotionOAuthService
{
    bool IsConfigured { get; }
    string CreateAuthorizationUrl(string state);
    Task<NotionOAuthConnectionResult> CompleteAuthorizationAsync(
        string code,
        CancellationToken cancellationToken = default);
    Task<NotionOAuthConnectionResult> RefreshAsync(CancellationToken cancellationToken = default);
    Task<NotionOAuthConnectionResult> DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed record NotionPickerItem(string Id, string Title, string ObjectType, string? ParentId);

public sealed record NotionPickerResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<NotionPickerItem> Items);

public sealed record NotionFileDownload(string FileName, string ContentType, byte[] Content);

public sealed record NotionMarkdownPage(string Markdown, bool Truncated, IReadOnlyList<string> UnknownBlockIds);

// Raw HTTP client over Notion's public API - Bearer token passed per-call (never held as
// service state) since the token comes from a DB row the caller already loaded, same as
// CjConnectionRequest.DeveloperKey being passed into ICjAffiliateService per-call.
public interface INotionService
{
    Task<NotionValidationResult> ValidateConnectionAsync(string integrationToken, CancellationToken cancellationToken = default);
    Task<NotionPage> SearchAsync(string integrationToken, string? cursor, CancellationToken cancellationToken = default);
    Task<NotionPage> GetBlockChildrenAsync(string integrationToken, string blockId, string? cursor, CancellationToken cancellationToken = default);
    Task<JsonElement?> GetPageAsync(string integrationToken, string pageId, CancellationToken cancellationToken = default);
    Task<NotionMarkdownPage?> GetPageMarkdownAsync(string integrationToken, string pageId, CancellationToken cancellationToken = default);
    Task<JsonElement?> GetDatabaseAsync(string integrationToken, string databaseId, CancellationToken cancellationToken = default);
    Task<NotionPage> QueryDatabaseAsync(
        string integrationToken,
        string databaseId,
        string? cursor,
        DateTimeOffset? editedAfter = null,
        CancellationToken cancellationToken = default);
    Task<NotionPage> ListViewsAsync(
        string integrationToken,
        string? databaseId,
        string? dataSourceId,
        string? cursor,
        CancellationToken cancellationToken = default);
    Task<JsonElement?> GetViewAsync(string integrationToken, string viewId, CancellationToken cancellationToken = default);
    Task<NotionPage> ListCommentsAsync(string integrationToken, string blockId, string? cursor, CancellationToken cancellationToken = default);
    Task<NotionFileDownload> DownloadFileAsync(string fileUrl, CancellationToken cancellationToken = default);
    Task UpdatePageAsync(string integrationToken, string pageId, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken = default);
    Task ReplaceBlockChildrenAsync(string integrationToken, string blockId, IReadOnlyList<object> children, CancellationToken cancellationToken = default);
    Task UpdateDataSourcePropertiesAsync(string integrationToken, string dataSourceId, IReadOnlyDictionary<string, object?> properties, CancellationToken cancellationToken = default);
}

public sealed class NotionConnectorSettingsView
{
    public string IntegrationToken { get; set; } = string.Empty;
    public bool HasStoredIntegrationToken { get; set; }
    public string AuthenticationMode { get; set; } = "internal";
    public bool IsOAuthConnected { get; set; }
    public string? WorkspaceId { get; set; }
    public string? WorkspaceIconUrl { get; set; }
    public string? WorkspaceName { get; set; }
    public bool AutoSyncEnabled { get; set; } = true;
    public string SyncDirection { get; set; } = "import";
    public string SelectedNotionIds { get; set; } = string.Empty;
    public bool AllowTwoWayWrites { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public int LastSyncImportedCount { get; set; }
    public int LastSyncUpdatedCount { get; set; }
    public int LastSyncArchivedCount { get; set; }
    public int LastSyncDiscoveredCount { get; set; }
    public int LastSyncSkippedCount { get; set; }
    public int LastSyncEmptyContentCount { get; set; }
    public int LastSyncContentBlockCount { get; set; }
    public string WebhookVerificationToken { get; set; } = string.Empty;
    public bool HasWebhookVerificationToken { get; set; }
    public DateTimeOffset? WebhookVerificationReceivedAt { get; set; }
    public DateTimeOffset? LastWebhookReceivedAt { get; set; }
    public string? LastWebhookEventType { get; set; }

    // True when the stored token's ciphertext could not be decrypted (Data Protection key
    // ring rotated since it was saved) - mirrors CjConnectorSettingsView's
    // DeveloperKeyUnreadable so the UI can prompt for re-entry instead of silently failing.
    public bool IntegrationTokenUnreadable { get; set; }
}

public sealed record NotionSyncResult(
    bool IsSuccess,
    string Message,
    int Imported,
    int Updated,
    int Archived,
    int Discovered = 0,
    int Skipped = 0,
    int EmptyContent = 0,
    int ContentBlocks = 0,
    // A page or database that threw while syncing is now logged and skipped rather than
    // aborting the whole run (and, with it, the sync watermark - see NotionSyncService.SyncAsync)
    // - this surfaces that something was skipped so it isn't silently invisible to whoever
    // triggered the sync, even though the run as a whole still reports success.
    int FailedItems = 0);

public static class NotionSyncJobStates
{
    public const string Idle = "idle";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
}

// Total is the count of pages/databases that actually need a content/schema fetch this run
// (known once the cheap discovery+reconcile pass finishes, before the expensive per-item
// loop starts) - not the full discovered workspace, most of which is skipped as unchanged.
public sealed record NotionSyncProgress(int Processed, int Total, string? CurrentItemTitle = null);

public sealed record NotionSyncJobStatus(
    string State,
    string Source,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    NotionSyncResult? Result,
    NotionSyncProgress? Progress = null)
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
    bool TryQueueWebhookSync();
    NotionSyncJobStatus GetStatus();
}

public sealed record NotionWebhookHandleResult(int StatusCode, string Message);

public static class NotionConflictResolutions
{
    public const string KeepSentinel = "keepSentinel";
    public const string UseNotion = "useNotion";
}

public sealed record NotionSyncConflictView(
    Guid Id,
    Guid WikiPageId,
    string PageTitle,
    string FieldName,
    string LocalValueJson,
    string RemoteValueJson,
    DateTimeOffset RemoteEditedAt,
    DateTimeOffset DetectedAt);

public interface INotionWebhookService
{
    Task<NotionWebhookHandleResult> HandleAsync(
        string rawBody,
        string? signature,
        CancellationToken cancellationToken = default);
}

// Reconciliation (search -> upsert pages/databases -> wire hierarchy -> sync blocks/rows) and
// connector-settings CRUD, mirroring how ICjAdsService bundles both connector settings and
// sync operations behind one interface rather than splitting settings into their own service.
public interface INotionSyncService
{
    Task<NotionConnectorSettingsView?> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<NotionPickerResult> BrowseAsync(string integrationToken, CancellationToken cancellationToken = default);
    Task<NotionValidationResult> SaveSettingsAsync(NotionConnectorSettingsView settings, CancellationToken cancellationToken = default);
    Task<NotionSyncResult> SyncAsync(CancellationToken cancellationToken = default);
    Task<NotionSyncResult> SyncAsync(bool forceRefresh, Action<NotionSyncProgress>? onProgress = null, CancellationToken cancellationToken = default);
    Task<NotionSyncResult> PushPageAsync(Guid wikiPageId, CancellationToken cancellationToken = default);
    Task<NotionSyncResult> PushDatabaseRowAsync(Guid wikiDatabaseRowId, CancellationToken cancellationToken = default);
    Task<NotionSyncResult> PushDatabaseSchemaAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default);
    Task ResetWebhookVerificationAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotionSyncConflictView>> GetPendingConflictsAsync(CancellationToken cancellationToken = default);
    Task ResolveConflictAsync(Guid conflictId, string resolution, string resolvedBy, CancellationToken cancellationToken = default);
}
