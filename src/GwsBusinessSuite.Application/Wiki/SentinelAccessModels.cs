namespace GwsBusinessSuite.Application.Wiki;

public sealed record SentinelPermissionView(Guid Id, string Username, string AccessLevel);

public sealed record SentinelShareView(
    Guid Id,
    Guid TargetId,
    bool IsDatabase,
    string? PublicToken,
    DateTimeOffset? ExpiresAt,
    bool AllowSearchIndexing,
    bool IsRevoked,
    bool RequiresPassword = false,
    int ViewCount = 0,
    DateTimeOffset? LastAccessedAt = null,
    bool IsAutomationWorkflow = false);

public sealed record SentinelAccessSnapshot(
    IReadOnlyList<SentinelPermissionView> Permissions,
    IReadOnlyList<SentinelShareView> Shares);

// Value object used by batch authorization checks. Page and database IDs are normally globally
// unique, but IsDatabase remains part of the identity because permissions are scoped by both
// columns and callers commonly combine both resource kinds in one workspace tree/search result.
public readonly record struct SentinelAccessTarget(Guid TargetId, bool IsDatabase);

public interface ISentinelAccessService
{
    Task<SentinelAccessSnapshot> GetAccessAsync(Guid targetId, bool isDatabase, CancellationToken cancellationToken = default);
    Task SetPermissionAsync(Guid targetId, bool isDatabase, string username, string accessLevel, string performedBy, CancellationToken cancellationToken = default);
    Task RemovePermissionAsync(Guid permissionId, string performedBy, CancellationToken cancellationToken = default);
    Task<SentinelShareView> CreatePublicShareAsync(
        Guid targetId, bool isDatabase, DateTimeOffset? expiresAt, bool allowSearchIndexing, string? password, string performedBy,
        bool isAutomationWorkflow = false, CancellationToken cancellationToken = default);
    Task RevokePublicShareAsync(Guid shareId, string performedBy, CancellationToken cancellationToken = default);
    Task<SentinelShareView?> ResolvePublicShareAsync(string token, CancellationToken cancellationToken = default);
    // Separate from ResolvePublicShareAsync so a caller can show/gate a password prompt
    // before deciding whether the visitor may see the share's target content at all.
    Task<bool> VerifySharePasswordAsync(Guid shareId, string password, CancellationToken cancellationToken = default);
    // Called once the visitor actually sees the target content (after any password gate
    // clears) - separate from ResolvePublicShareAsync so a metadata check or a failed
    // password attempt is never counted as a view.
    Task RecordShareViewAsync(Guid shareId, CancellationToken cancellationToken = default);
    Task<bool> CanAccessAsync(Guid targetId, bool isDatabase, string username, string requiredAccessLevel, CancellationToken cancellationToken = default);
    // Resolves a complete target set in one graph/permission load so tree and search callers do
    // not issue one authorization query per item. A direct target permission wins; otherwise the
    // nearest page-ancestor permission is inherited. Broken/cyclic ancestry fails closed.
    Task<IReadOnlySet<SentinelAccessTarget>> GetAccessibleTargetsAsync(
        IReadOnlyCollection<SentinelAccessTarget> targets,
        string username,
        string requiredAccessLevel,
        CancellationToken cancellationToken = default);
}
