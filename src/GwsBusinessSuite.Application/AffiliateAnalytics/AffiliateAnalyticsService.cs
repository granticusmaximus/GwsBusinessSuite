using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.AffiliateAnalytics;

public sealed class AffiliateAnalyticsService(IAppDbContext db) : IAffiliateAnalyticsService
{
    public async Task<string?> RecordClickAsync(Guid placementId, CancellationToken cancellationToken = default)
    {
        var placement = await db.ArticleAffiliatePlacements
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == placementId, cancellationToken);

        if (placement is null)
        {
            return null;
        }

        await db.ArticleAffiliateClicks.AddAsync(new ArticleAffiliateClick
        {
            ArticleId = placement.ArticleId,
            PlacementId = placement.Id,
            AdvertiserId = placement.AdvertiserId,
            AdvertiserName = placement.AdvertiserName,
            TrackingUrl = placement.TrackingUrl,
            CreatedBy = "affiliate-click-redirect"
        }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(placement.TrackingUrl) ? null : placement.TrackingUrl;
    }

    public async Task<AffiliateAnalyticsDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var clicks = await db.ArticleAffiliateClicks
            .AsNoTracking()
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
