using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Application.AffiliateAnalytics;

public sealed class AffiliateAnalyticsService(IAppDbContext db, IMemoryCache cache) : IAffiliateAnalyticsService
{
    // A repeat hit on the same placement within this window (browser back-button,
    // refresh, prefetch, or a quick re-click) doesn't get its own click row - it still
    // redirects normally, just isn't logged again. This intentionally doesn't key on
    // client IP (no PII collection for what's an internal analytics feature) and isn't
    // fraud-grade abuse protection - it only smooths out the cheap, common
    // double-counting cases. A DB-side "was there a recent click for this placement"
    // query was deliberately avoided here since it would either scan full per-placement
    // click history (SQLite/EF Core can't push a CreatedAt >= cutoff filter to SQL for
    // DateTimeOffset columns - see the CrmService/AffiliateSuggestionService comments on
    // the same limitation) or require pulling the whole table down on every single click.
    private static readonly TimeSpan ClickDedupeWindow = TimeSpan.FromMinutes(30);

    public async Task<string?> RecordClickAsync(Guid placementId, CancellationToken cancellationToken = default)
    {
        var placement = await db.ArticleAffiliatePlacements
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == placementId, cancellationToken);

        if (placement is null)
        {
            var rotation = await db.ArticleAffiliateRotations
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == placementId, cancellationToken);
            if (rotation is null)
            {
                return null;
            }

            return await RecordClickAsync(
                rotation.Id,
                rotation.ArticleId,
                rotation.AdvertiserId,
                rotation.AdvertiserName,
                rotation.TrackingUrl,
                cancellationToken);
        }

        return await RecordClickAsync(
            placement.Id,
            placement.ArticleId,
            placement.AdvertiserId,
            placement.AdvertiserName,
            placement.TrackingUrl,
            cancellationToken);
    }

    private async Task<string?> RecordClickAsync(
        Guid placementId,
        Guid articleId,
        string advertiserId,
        string advertiserName,
        string trackingUrl,
        CancellationToken cancellationToken)
    {
        var dedupeCacheKey = $"affiliate-click-dedupe:{placementId}";
        if (!cache.TryGetValue(dedupeCacheKey, out _))
        {
            cache.Set(dedupeCacheKey, true, ClickDedupeWindow);

            var now = DateTimeOffset.UtcNow;
            await db.ArticleAffiliateClicks.AddAsync(new ArticleAffiliateClick
            {
                ArticleId = articleId,
                PlacementId = placementId,
                AdvertiserId = advertiserId,
                AdvertiserName = advertiserName,
                TrackingUrl = trackingUrl,
                CreatedAt = now,
                CreatedAtUnixSeconds = now.ToUnixTimeSeconds(),
                CreatedBy = "affiliate-click-redirect"
            }, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return string.IsNullOrWhiteSpace(trackingUrl) ? null : trackingUrl;
    }

    // Both this table and CjCommissionRecords grow with every affiliate click/commission this
    // site has ever recorded - previously loaded in full on every dashboard view, with no date
    // bound and no row cap. CreatedAtUnixSeconds (a shadow column - SQLite/EF Core can't push
    // a DateTimeOffset range filter to SQL, see the note further down) lets this bound both
    // queries at the SQL level: recent-first, capped at MaxDashboardRows. A dashboard showing
    // "the last 90 days, up to 5,000 rows" is the actually useful view anyway - nobody scrolls
    // through years of raw click history looking for a trend.
    private static readonly TimeSpan DashboardWindow = TimeSpan.FromDays(90);
    private const int MaxDashboardRows = 5_000;

    public async Task<AffiliateAnalyticsDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var cutoffUnixSeconds = DateTimeOffset.UtcNow.Subtract(DashboardWindow).ToUnixTimeSeconds();
        var clicks = await db.ArticleAffiliateClicks
            .AsNoTracking()
            .Where(c => c.CreatedAtUnixSeconds >= cutoffUnixSeconds)
            .OrderByDescending(c => c.CreatedAtUnixSeconds)
            .Take(MaxDashboardRows)
            .ToListAsync(cancellationToken);

        var articleIds = clicks.Select(c => c.ArticleId).Distinct().ToList();
        var articles = await db.Articles
            .AsNoTracking()
            .Where(a => articleIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Title, a.Slug })
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        // SQLite can't translate ORDER BY on a DateTimeOffset column, so all of this
        // grouping/ordering happens in memory over the already-materialized `clicks` list.
        var clicksByAdvertiser = clicks
            .GroupBy(c => (c.AdvertiserId, c.AdvertiserName))
            .Select(g => new AdvertiserClickSummary(g.Key.AdvertiserId, g.Key.AdvertiserName, g.Count(), g.Max(c => c.CreatedAt)))
            .OrderByDescending(s => s.ClickCount)
            .ToList();

        var clicksByArticle = clicks
            .GroupBy(c => c.ArticleId)
            .Select(g => articles.TryGetValue(g.Key, out var article)
                ? new ArticleClickSummary(g.Key, article.Title, article.Slug, g.Count())
                : new ArticleClickSummary(g.Key, "(deleted article)", string.Empty, g.Count()))
            .OrderByDescending(s => s.ClickCount)
            .ToList();

        var recentClicks = clicks
            .OrderByDescending(c => c.CreatedAt)
            .Take(50)
            .Select(c =>
            {
                var article = articles.GetValueOrDefault(c.ArticleId);
                return new ArticleAffiliateClickView(c.Id, c.AdvertiserName, article?.Title ?? "(deleted article)", article?.Slug ?? string.Empty, c.CreatedAt);
            })
            .ToList();

        var commissions = await db.CjCommissionRecords
            .AsNoTracking()
            .Where(c => c.CreatedAtUnixSeconds >= cutoffUnixSeconds)
            .OrderByDescending(c => c.CreatedAtUnixSeconds)
            .Take(MaxDashboardRows)
            .ToListAsync(cancellationToken);

        var revenueByAdvertiser = commissions
            .GroupBy(c => (c.AdvertiserId, c.AdvertiserName))
            .Select(g => new AdvertiserRevenueSummary(
                g.Key.AdvertiserId,
                g.Key.AdvertiserName,
                g.Count(),
                g.Sum(c => c.SaleAmount),
                g.Sum(c => c.CommissionAmount),
                g.Select(c => c.Currency).FirstOrDefault(currency => !string.IsNullOrWhiteSpace(currency)) ?? "USD"))
            .OrderByDescending(s => s.TotalCommissionAmount)
            .ToList();

        return new AffiliateAnalyticsDashboard
        {
            TotalClicks = clicks.Count,
            TotalCommissionAmount = commissions.Sum(c => c.CommissionAmount),
            ClicksByAdvertiser = clicksByAdvertiser,
            ClicksByArticle = clicksByArticle,
            RecentClicks = recentClicks,
            RevenueByAdvertiser = revenueByAdvertiser
        };
    }
}
