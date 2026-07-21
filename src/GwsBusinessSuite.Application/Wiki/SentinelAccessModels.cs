namespace GwsBusinessSuite.Application.Wiki;

public sealed record SentinelPermissionView(Guid Id, string Username, string AccessLevel);

public sealed record SentinelShareView(
    Guid Id,
    Guid TargetId,
    bool IsDatabase,
    string? PublicToken,
    DateTimeOffset? ExpiresAt,
    bool AllowSearchIndexing,
    bool IsRevoked);

public sealed record SentinelAccessSnapshot(
    IReadOnlyList<SentinelPermissionView> Permissions,
    IReadOnlyList<SentinelShareView> Shares);

public interface ISentinelAccessService
{
    Task<SentinelAccessSnapshot> GetAccessAsync(Guid targetId, bool isDatabase, CancellationToken cancellationToken = default);
    Task SetPermissionAsync(Guid targetId, bool isDatabase, string username, string accessLevel, string performedBy, CancellationToken cancellationToken = default);
    Task RemovePermissionAsync(Guid permissionId, string performedBy, CancellationToken cancellationToken = default);
    Task<SentinelShareView> CreatePublicShareAsync(Guid targetId, bool isDatabase, DateTimeOffset? expiresAt, bool allowSearchIndexing, string performedBy, CancellationToken cancellationToken = default);
    Task RevokePublicShareAsync(Guid shareId, string performedBy, CancellationToken cancellationToken = default);
    Task<SentinelShareView?> ResolvePublicShareAsync(string token, CancellationToken cancellationToken = default);
    Task<bool> CanAccessAsync(Guid targetId, bool isDatabase, string username, string requiredAccessLevel, CancellationToken cancellationToken = default);
}
