namespace GwsBusinessSuite.Application.DeveloperApi;

public static class DeveloperApiScopes
{
    public const string ContactsRead = "contacts:read";
    public const string ContactsWrite = "contacts:write";
    public const string DealsRead = "deals:read";
    public const string DealsWrite = "deals:write";
    public const string CmsPagesRead = "cms-pages:read";
    public const string CmsPagesWrite = "cms-pages:write";
    // Deliberately has no "sentinel:write" counterpart - see NativeToolExecutor (App project)
    // for why the native Mac SentinelGPT tab's tool set is permanently read-only.
    public const string SentinelRead = "sentinel:read";

    public static readonly string[] All =
    [
        ContactsRead, ContactsWrite,
        DealsRead, DealsWrite,
        CmsPagesRead, CmsPagesWrite,
        SentinelRead
    ];
}

public sealed record DeveloperApiKeyView(
    Guid Id,
    string Name,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    int RateLimitPerMinute,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    long RequestCount,
    DateTimeOffset? RevokedAt);

public sealed record IssuedDeveloperApiKey(DeveloperApiKeyView Key, string PlaintextKey);

public sealed record AuthenticatedDeveloperApiKey(
    Guid Id,
    string Name,
    IReadOnlyList<string> Scopes,
    int RateLimitPerMinute,
    string CreatedBy);

public interface IDeveloperApiKeyService
{
    Task<IReadOnlyList<DeveloperApiKeyView>> ListAsync(CancellationToken cancellationToken = default);
    Task<IssuedDeveloperApiKey> IssueAsync(
        string name,
        IReadOnlyCollection<string> scopes,
        int rateLimitPerMinute,
        DateTimeOffset? expiresAt,
        string performedBy,
        CancellationToken cancellationToken = default);
    Task RevokeAsync(Guid id, string performedBy, CancellationToken cancellationToken = default);
    Task<AuthenticatedDeveloperApiKey?> AuthenticateAsync(string plaintextKey, CancellationToken cancellationToken = default);
}
