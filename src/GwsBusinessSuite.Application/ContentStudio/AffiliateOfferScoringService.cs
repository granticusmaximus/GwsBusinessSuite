using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.ContentStudio;

public sealed class AffiliateOfferScoringService(IAppDbContext db) : IAffiliateOfferScoringService
{
    public async Task<IReadOnlyList<ScoredAffiliateOfferView>> ScoreOffersAsync(
        ArticleGenerationRequest request,
        int maxOffers,
        CancellationToken cancellationToken = default)
    {
        if (maxOffers <= 0)
        {
            return Array.Empty<ScoredAffiliateOfferView>();
        }

        var offers = await db.AffiliateOffers
            .AsNoTracking()
            .Where(x => x.Network == "CJ")
            .ToListAsync(cancellationToken);

        if (offers.Count == 0)
        {
            return Array.Empty<ScoredAffiliateOfferView>();
        }

        var terms = BuildTerms(request);
        var performance = await db.SeoArticleAffiliateInteractions
            .AsNoTracking()
            .GroupBy(x => x.AdvertiserId)
            .Select(group => new
            {
                AdvertiserId = group.Key,
                Impressions = group.Count(x => x.EventType == AffiliateInteractionEventTypes.Impression),
                Clicks = group.Count(x => x.EventType == AffiliateInteractionEventTypes.Click)
            })
            .ToDictionaryAsync(x => x.AdvertiserId, x => (x.Impressions, x.Clicks), StringComparer.OrdinalIgnoreCase, cancellationToken);

        var scored = SelectOffersForScoring(offers)
            .Select(offer => new
            {
                Offer = offer,
                Score = ScoreOffer(offer.AdvertiserId, offer.AdvertiserName, offer.Category, terms, performance)
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Offer.UpdatedAt ?? x.Offer.CreatedAt)
            .ThenBy(x => x.Offer.AdvertiserName)
            .Take(maxOffers)
            .Select(x => new ScoredAffiliateOfferView
            {
                AdvertiserId = x.Offer.AdvertiserId,
                AdvertiserName = x.Offer.AdvertiserName,
                Category = x.Offer.Category ?? string.Empty,
                TrackingUrl = x.Offer.TrackingUrl ?? string.Empty,
                Score = x.Score
            })
            .ToArray();

        return scored;
    }

    private static HashSet<string> BuildTerms(ArticleGenerationRequest request)
    {
        var raw = string.Join(' ', request.Topic, request.TargetAudience, request.PrimaryKeyword, request.SecondaryKeywords);
        return raw
            .Split([' ', ',', '.', ';', ':', '|', '/', '\\', '-', '_', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static double ScoreOffer(
        string advertiserId,
        string advertiserName,
        string? category,
        HashSet<string> terms,
        IReadOnlyDictionary<string, (int Impressions, int Clicks)> performance)
    {
        if (terms.Count == 0)
        {
            return ScorePerformance(advertiserId, performance);
        }

        var name = advertiserName.ToLowerInvariant();
        var cat = (category ?? string.Empty).ToLowerInvariant();

        var score = 0d;
        foreach (var term in terms)
        {
            if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }

            if (!string.IsNullOrWhiteSpace(cat) && cat.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }
        }

        return score + ScorePerformance(advertiserId, performance);
    }

    private static IReadOnlyList<AffiliateOffer> SelectOffersForScoring(IReadOnlyList<AffiliateOffer> offers)
    {
        return offers
            .GroupBy(offer => offer.AdvertiserId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var catalogOffers = group.Where(IsCatalogOffer).ToList();
                return catalogOffers.Count > 0 ? catalogOffers : group.ToList();
            })
            .ToList();
    }

    private static bool IsCatalogOffer(AffiliateOffer offer)
    {
        return !string.Equals(offer.LinkName, offer.AdvertiserId, StringComparison.OrdinalIgnoreCase);
    }

    private static double ScorePerformance(
        string advertiserId,
        IReadOnlyDictionary<string, (int Impressions, int Clicks)> performance)
    {
        if (!performance.TryGetValue(advertiserId, out var metrics))
        {
            return 0;
        }

        var ctr = metrics.Impressions == 0
            ? 0
            : (double)metrics.Clicks / metrics.Impressions;

        return (metrics.Clicks * 1.5d) + (ctr * 10d);
    }
}
