namespace GwsBusinessSuite.Application.AffiliateAnalytics;

public sealed class AffiliateAnalyticsDashboard
{
    public int TotalClicks { get; init; }
    public decimal TotalCommissionAmount { get; init; }
    public IReadOnlyList<AdvertiserClickSummary> ClicksByAdvertiser { get; init; } = Array.Empty<AdvertiserClickSummary>();
    public IReadOnlyList<ArticleClickSummary> ClicksByArticle { get; init; } = Array.Empty<ArticleClickSummary>();
    public IReadOnlyList<ArticleAffiliateClickView> RecentClicks { get; init; } = Array.Empty<ArticleAffiliateClickView>();
    public IReadOnlyList<AdvertiserRevenueSummary> RevenueByAdvertiser { get; init; } = Array.Empty<AdvertiserRevenueSummary>();
}

public sealed record AdvertiserClickSummary(string AdvertiserId, string AdvertiserName, int ClickCount, DateTimeOffset LastClickAt);

public sealed record ArticleClickSummary(Guid ArticleId, string ArticleTitle, string ArticleSlug, int ClickCount);

public sealed record ArticleAffiliateClickView(Guid Id, string AdvertiserName, string ArticleTitle, string ArticleSlug, DateTimeOffset CreatedAt);

public sealed record AdvertiserRevenueSummary(
    string AdvertiserId,
    string AdvertiserName,
    int TransactionCount,
    decimal TotalSaleAmount,
    decimal TotalCommissionAmount,
    string Currency);

public sealed record CommissionSyncResult(bool IsSuccess, string Message, int RecordsImported);
