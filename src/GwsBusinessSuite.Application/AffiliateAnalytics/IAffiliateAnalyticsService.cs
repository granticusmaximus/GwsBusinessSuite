namespace GwsBusinessSuite.Application.AffiliateAnalytics;

public interface IAffiliateAnalyticsService
{
    // Looks up the placement, logs a click row, and returns the URL to redirect the
    // reader to - null if the placement no longer exists (article edited/unpublished).
    Task<string?> RecordClickAsync(Guid placementId, CancellationToken cancellationToken = default);

    Task<AffiliateAnalyticsDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);
}
