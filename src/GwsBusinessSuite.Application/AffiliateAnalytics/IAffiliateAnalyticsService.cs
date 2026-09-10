namespace GwsBusinessSuite.Application.AffiliateAnalytics;

public interface IAffiliateAnalyticsService
{
    // Looks up a manual placement or durable rotation assignment, logs a click row, and
    // returns the URL to redirect the reader to. userAgent/prefetchHeader classify the click
    // as bot traffic or a real reader (see AffiliateClickFilter) before it's counted - the
    // redirect still happens either way, only the analytics count is affected.
    Task<string?> RecordClickAsync(
        Guid placementId,
        string? userAgent,
        string? prefetchHeader,
        CancellationToken cancellationToken = default);

    Task<AffiliateAnalyticsDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);
}
