using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Articles;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.AffiliateRotations;

public sealed class AffiliateRotationService(
    IAppDbContext db,
    TimeProvider timeProvider,
    ILogger<AffiliateRotationService> logger) : IAffiliateRotationService
{
    public static readonly TimeSpan RotationWindow = TimeSpan.FromHours(48);
    private static readonly SemaphoreSlim AssignmentLock = new(1, 1);

    public async Task<ArticleMarkdownRenderer.AffiliatePlacementMarkup?> GetActivePlacementAsync(
        Guid articleId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken))
        {
            return null;
        }

        var nowUnixSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var rotation = await FindActiveRotationAsync(articleId, nowUnixSeconds, cancellationToken);
        if (rotation is null)
        {
            await RefreshAsync(cancellationToken: cancellationToken);
            rotation = await FindActiveRotationAsync(articleId, nowUnixSeconds, cancellationToken);
        }

        return rotation is null ? null : ToMarkup(rotation);
    }

    public async Task<AffiliateRotationRefreshResult> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await AssignmentLock.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var nowUnixSeconds = now.ToUnixTimeSeconds();
            var activeArticleIds = await db.Articles
                .AsNoTracking()
                .Where(article => article.TrashedAt == null
                    && article.Status == ArticleStatuses.Published
                    && article.PublishedAtUnixSeconds != null
                    && article.PublishedAtUnixSeconds <= nowUnixSeconds)
                .Select(article => article.Id)
                .ToListAsync(cancellationToken);

            if (!await IsEnabledAsync(cancellationToken))
            {
                return new AffiliateRotationRefreshResult(
                    false, activeArticleIds.Count, 0, 0, 0,
                    "Automatic 48-hour blog ad rotation is paused.");
            }

            var eligibleOffers = await LoadEligibleOffersAsync(now, cancellationToken);
            if (activeArticleIds.Count == 0 || eligibleOffers.Count == 0)
            {
                return new AffiliateRotationRefreshResult(
                    true, activeArticleIds.Count, eligibleOffers.Count, 0, 0,
                    eligibleOffers.Count == 0
                        ? "No eligible CJ links are available. Add Website ID and sync advertiser links first."
                        : "There are no active published blog posts to assign.");
            }

            var activeRotations = await db.ArticleAffiliateRotations
                .Where(rotation => activeArticleIds.Contains(rotation.ArticleId)
                    && rotation.EndedAtUnixSeconds == null
                    && rotation.StartsAtUnixSeconds <= nowUnixSeconds
                    && rotation.ExpiresAtUnixSeconds > nowUnixSeconds)
                .OrderByDescending(rotation => rotation.StartsAtUnixSeconds)
                .ToListAsync(cancellationToken);

            var currentByArticle = activeRotations
                .GroupBy(rotation => rotation.ArticleId)
                .ToDictionary(group => group.Key, group => group.First());

            var articleIdsToAssign = force
                ? activeArticleIds
                : activeArticleIds.Where(articleId => !currentByArticle.ContainsKey(articleId)).ToList();

            if (force)
            {
                foreach (var rotation in activeRotations)
                {
                    rotation.EndedAt = now;
                    rotation.EndedAtUnixSeconds = nowUnixSeconds;
                    rotation.UpdatedAt = now;
                    rotation.UpdatedBy = "affiliate-rotation";
                }
            }

            var usageCounts = await db.ArticleAffiliateRotations
                .AsNoTracking()
                .GroupBy(rotation => rotation.AffiliateOfferId)
                .Select(group => new { OfferId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.OfferId, item => item.Count, cancellationToken);

            var previousByArticle = await db.ArticleAffiliateRotations
                .AsNoTracking()
                .Where(rotation => articleIdsToAssign.Contains(rotation.ArticleId))
                .OrderByDescending(rotation => rotation.StartsAtUnixSeconds)
                .ToListAsync(cancellationToken);

            var lastAdvertiserByArticle = previousByArticle
                .GroupBy(rotation => rotation.ArticleId)
                .ToDictionary(group => group.Key, group => group.First().AdvertiserId);

            var expiresAt = now.Add(RotationWindow);
            foreach (var articleId in articleIdsToAssign)
            {
                var candidates = eligibleOffers.AsEnumerable();
                if (lastAdvertiserByArticle.TryGetValue(articleId, out var previousAdvertiser)
                    && eligibleOffers.Any(offer => !string.Equals(offer.AdvertiserId, previousAdvertiser, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates = candidates.Where(offer =>
                        !string.Equals(offer.AdvertiserId, previousAdvertiser, StringComparison.OrdinalIgnoreCase));
                }

                var selected = candidates
                    .OrderBy(offer => usageCounts.GetValueOrDefault(offer.Id))
                    .ThenBy(offer => offer.AdvertiserName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(offer => offer.LinkName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(offer => offer.Id)
                    .First();

                db.ArticleAffiliateRotations.Add(new ArticleAffiliateRotation
                {
                    ArticleId = articleId,
                    AffiliateOfferId = selected.Id,
                    AdvertiserId = selected.AdvertiserId,
                    AdvertiserName = selected.AdvertiserName,
                    LinkName = selected.LinkName,
                    Category = selected.Category ?? string.Empty,
                    TrackingUrl = selected.TrackingUrl!,
                    StartsAt = now,
                    StartsAtUnixSeconds = nowUnixSeconds,
                    ExpiresAt = expiresAt,
                    ExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds(),
                    CreatedAt = now,
                    CreatedBy = "affiliate-rotation"
                });
                usageCounts[selected.Id] = usageCounts.GetValueOrDefault(selected.Id) + 1;
            }

            if (articleIdsToAssign.Count > 0 || (force && activeRotations.Count > 0))
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation(
                "Affiliate rotation refresh assigned {Created} and preserved {Preserved} article(s) from {EligibleOffers} eligible CJ links.",
                articleIdsToAssign.Count,
                force ? 0 : activeArticleIds.Count - articleIdsToAssign.Count,
                eligibleOffers.Count);

            return new AffiliateRotationRefreshResult(
                true,
                activeArticleIds.Count,
                eligibleOffers.Count,
                articleIdsToAssign.Count,
                force ? 0 : activeArticleIds.Count - articleIdsToAssign.Count,
                articleIdsToAssign.Count == 0
                    ? "Every active blog post already has a current 48-hour CJ ad assignment."
                    : $"Assigned {articleIdsToAssign.Count} active blog post(s). These ads remain stable for 48 hours.");
        }
        finally
        {
            AssignmentLock.Release();
        }
    }

    public async Task<AffiliateRotationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var nowUnixSeconds = now.ToUnixTimeSeconds();
        var enabled = await IsEnabledAsync(cancellationToken);
        var activeArticleCount = await db.Articles
            .AsNoTracking()
            .CountAsync(article => article.TrashedAt == null
                && article.Status == ArticleStatuses.Published
                && article.PublishedAtUnixSeconds != null
                && article.PublishedAtUnixSeconds <= nowUnixSeconds, cancellationToken);
        var eligibleOfferCount = (await LoadEligibleOffersAsync(now, cancellationToken)).Count;
        var current = await db.ArticleAffiliateRotations
            .AsNoTracking()
            .Where(rotation => rotation.EndedAtUnixSeconds == null
                && rotation.StartsAtUnixSeconds <= nowUnixSeconds
                && rotation.ExpiresAtUnixSeconds > nowUnixSeconds)
            .ToListAsync(cancellationToken);

        return new AffiliateRotationStatus(
            enabled,
            activeArticleCount,
            eligibleOfferCount,
            current.Select(rotation => rotation.ArticleId).Distinct().Count(),
            current.Count == 0 ? null : current.Min(rotation => rotation.ExpiresAt));
    }

    private async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await db.CjConnectorSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return settings?.AutomaticArticleRotationEnabled ?? true;
    }

    private async Task<List<AffiliateOffer>> LoadEligibleOffersAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cjRows = await db.AffiliateOffers
            .AsNoTracking()
            .Where(offer => offer.Network == "CJ")
            .ToListAsync(cancellationToken);

        var connectedAdvertiserIds = cjRows
            .Where(offer => string.Equals(offer.LinkName, offer.AdvertiserId, StringComparison.OrdinalIgnoreCase)
                && IsJoinedRelationship(offer.RelationshipStatus))
            .Select(offer => offer.AdvertiserId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return cjRows
            .Where(offer => connectedAdvertiserIds.Contains(offer.AdvertiserId)
                && !string.Equals(offer.LinkName, offer.AdvertiserId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(offer.TrackingUrl)
                && (offer.PromotionEndsAt is null || offer.PromotionEndsAt > now))
            .GroupBy(offer => offer.TrackingUrl!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(offer => offer.UpdatedAt ?? offer.CreatedAt).First())
            .ToList();
    }

    private Task<ArticleAffiliateRotation?> FindActiveRotationAsync(
        Guid articleId,
        long nowUnixSeconds,
        CancellationToken cancellationToken) =>
        db.ArticleAffiliateRotations
            .AsNoTracking()
            .Where(rotation => rotation.ArticleId == articleId
                && rotation.EndedAtUnixSeconds == null
                && rotation.StartsAtUnixSeconds <= nowUnixSeconds
                && rotation.ExpiresAtUnixSeconds > nowUnixSeconds)
            .OrderByDescending(rotation => rotation.StartsAtUnixSeconds)
            .FirstOrDefaultAsync(cancellationToken);

    private static ArticleMarkdownRenderer.AffiliatePlacementMarkup ToMarkup(ArticleAffiliateRotation rotation) => new(
        string.Empty,
        rotation.AdvertiserName,
        rotation.Category,
        rotation.TrackingUrl,
        rotation.CallToActionText,
        rotation.Id);

    private static bool IsJoinedRelationship(string? status) =>
        status is not null && (status.Equals("joined", StringComparison.OrdinalIgnoreCase)
            || status.Equals("active", StringComparison.OrdinalIgnoreCase)
            || status.Equals("approved", StringComparison.OrdinalIgnoreCase));
}
