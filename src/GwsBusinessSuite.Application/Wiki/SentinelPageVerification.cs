namespace GwsBusinessSuite.Application.Wiki;

// Page verification, as in Slite/Notion wikis: someone confirms a page is accurate until a date.
// When that date passes, the page's owner (or whoever verified it) gets one bell notification and
// the page shows in "Needs review". A page with an owner that was never verified shows there too
// once nobody has edited it for StaleAfterDays. Pages with neither are ordinary pages and aren't
// tracked at all, so a big import doesn't flood the list.
public static class SentinelVerificationStates
{
    public const string Verified = "verified";
    public const string Expired = "expired";
    public const string Unverified = "unverified";
}

public static class SentinelNeedsReviewReasons
{
    public const string Expired = "expired";
    public const string NeverVerified = "neverVerified";
}

public sealed record SentinelPageVerificationView(
    Guid PageId,
    string? OwnerUsername,
    string? VerifiedBy,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? VerifiedUntil,
    string State);

public sealed record SentinelNeedsReviewItem(
    Guid PageId,
    string Title,
    string Reason,
    string? OwnerUsername,
    // When the verification lapsed, or when the page was last edited for NeverVerified.
    DateTimeOffset Since);

public static class SentinelPageVerificationRules
{
    public static readonly IReadOnlyList<int> PeriodDays = [30, 90, 180, 365];
    public const int StaleAfterDays = 180;
    public const string ExpiredNotificationKind = "verificationExpired";
    // Added to a currently-verified page's search score: enough to win a near-tie against an
    // unverified page, not enough to outrank a clearly better match (a title hit is worth 120+).
    public const int SearchBoost = 40;

    public static string State(long? verifiedUntilUnix, DateTimeOffset now) => verifiedUntilUnix switch
    {
        null => SentinelVerificationStates.Unverified,
        var until when until > now.ToUnixTimeSeconds() => SentinelVerificationStates.Verified,
        _ => SentinelVerificationStates.Expired
    };

    public static DateTimeOffset? FromUnix(long? seconds) =>
        seconds is { } value ? DateTimeOffset.FromUnixTimeSeconds(value) : null;
}

public interface ISentinelPageVerificationService
{
    Task<SentinelPageVerificationView?> GetAsync(Guid pageId, CancellationToken cancellationToken = default);

    // days must be one of SentinelPageVerificationRules.PeriodDays.
    Task<SentinelPageVerificationView> VerifyAsync(Guid pageId, int days, string username, CancellationToken cancellationToken = default);

    Task<SentinelPageVerificationView> UnverifyAsync(Guid pageId, string username, CancellationToken cancellationToken = default);

    // null clears the owner. The owner must be an active user.
    Task<SentinelPageVerificationView> SetOwnerAsync(Guid pageId, string? ownerUsername, string username, CancellationToken cancellationToken = default);

    // Pages the user can view that need review, lapsed ones first.
    Task<IReadOnlyList<SentinelNeedsReviewItem>> ListNeedsReviewAsync(string username, int maxResults = 50, CancellationToken cancellationToken = default);

    // Sends the one-time "verification expired" bell for every page that has lapsed since the
    // last sweep. Returns how many were sent.
    Task<int> NotifyLapsedAsync(CancellationToken cancellationToken = default);
}
