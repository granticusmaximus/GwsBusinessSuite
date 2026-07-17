namespace GwsBusinessSuite.Application.AffiliateAnalytics;

public interface IAffiliateAnalyticsService
{
    // Looks up a manual placement or durable rotation assignment, logs a click row, and
    // returns the URL to redirect the reader to.
    Task<string?> RecordClickAsync(Guid placementId, CancellationToken cancellationToken = default);

    Task<AffiliateAnalyticsDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);
}
