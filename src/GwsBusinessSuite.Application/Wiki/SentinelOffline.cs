namespace GwsBusinessSuite.Application.Wiki;

// Offline reading (Sentinel item 12), Mac app first. A person marks pages "Keep offline"; the Mac
// app downloads read-only copies of them (rendered HTML) and shows those when the server can't be
// reached, while Quick Notes made offline wait in the app's outbox. The web app itself stays
// online-only - a service worker would fight the Blazor Server circuit for little gain.
public static class SentinelOffline
{
    public const int MaxPagesPerUser = 200;
    // A page bigger than this as HTML isn't sent (it would mostly be embedded images).
    public const int MaxHtmlLength = 3_000_000;
}

public sealed record SentinelOfflinePageCopy(Guid Id, string Title, string? Icon, long UpdatedAtUnix, string Html);

public interface ISentinelOfflineService
{
    Task<bool> IsKeptOfflineAsync(Guid pageId, string username, CancellationToken cancellationToken = default);
    // Throws ArgumentException when the person can't view the page or is at the limit.
    Task SetKeptOfflineAsync(Guid pageId, string username, bool keep, CancellationToken cancellationToken = default);
    // The person's kept pages they can still view (trashed or no-longer-shared pages are left out).
    Task<IReadOnlyList<SentinelOfflinePageCopy>> GetCopiesAsync(string username, CancellationToken cancellationToken = default);
}
