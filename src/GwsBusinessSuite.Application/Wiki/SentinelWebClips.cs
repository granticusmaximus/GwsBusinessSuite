namespace GwsBusinessSuite.Application.Wiki;

// Web clipper (Sentinel item 8). The browser extension sends what the user clipped, authenticated
// by a per-user clip token; the server turns it into a page under "Web Clips" or a section of
// today's daily note. A clip token can only create clips - it is not a general Sentinel write API.
public static class SentinelWebClips
{
    public const string FolderSystemKey = "web-clips";
    public const string TokenPrefix = "gwsclip_";
    public const int MaxTokensPerUser = 10;
    // The raw page HTML the extension may send for an article clip (a long page is ~1-2 MB).
    public const int MaxHtmlLength = 4_000_000;
    public const int MaxNoteLength = 4_000;
    public const int MaxTitleLength = 200;

    public static class Modes
    {
        public const string Article = "article";
        public const string Selection = "selection";
        public const string Link = "link";
    }

    public static class Targets
    {
        public const string NewPage = "page";
        public const string Today = "today";
    }
}

public sealed record SentinelClipRequest(
    string Mode,
    string Url,
    string? Title,
    string? Html,
    string? Note,
    string? Target,
    IReadOnlyList<string>? Tags);

public sealed record SentinelClipResult(Guid PageId, string Title, string Target);

public sealed record SentinelClipTokenView(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, long? LastUsedAtUnix);

// Token is the full secret; it is only ever returned here, once.
public sealed record SentinelClipTokenCreated(SentinelClipTokenView View, string Token);

public sealed class SentinelClipException(string message) : Exception(message);

public interface ISentinelWebClipService
{
    Task<IReadOnlyList<SentinelClipTokenView>> ListTokensAsync(string username, CancellationToken cancellationToken = default);
    Task<SentinelClipTokenCreated> CreateTokenAsync(string username, string name, CancellationToken cancellationToken = default);
    Task RevokeTokenAsync(Guid tokenId, string username, CancellationToken cancellationToken = default);
    // The active user a token belongs to, or null for an unknown/revoked token or an inactive user.
    Task<string?> ResolveTokenAsync(string? token, CancellationToken cancellationToken = default);
    Task<SentinelClipResult> ClipAsync(string username, SentinelClipRequest request, CancellationToken cancellationToken = default);
}
