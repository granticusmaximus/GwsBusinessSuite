namespace GwsBusinessSuite.Application.ContentStudio;

public interface IAffiliateOfferScoringService
{
    Task<IReadOnlyList<ScoredAffiliateOfferView>> ScoreOffersAsync(
        ArticleGenerationRequest request,
        int maxOffers,
        CancellationToken cancellationToken = default);
}
