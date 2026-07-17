using GwsBusinessSuite.Application.Articles;

namespace GwsBusinessSuite.Application.AffiliateRotations;

public interface IAffiliateRotationService
{
    Task<ArticleMarkdownRenderer.AffiliatePlacementMarkup?> GetActivePlacementAsync(
        Guid articleId,
        CancellationToken cancellationToken = default);

    Task<AffiliateRotationRefreshResult> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default);

    Task<AffiliateRotationStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record AffiliateRotationRefreshResult(
    bool IsEnabled,
    int ActiveArticleCount,
    int EligibleOfferCount,
    int AssignmentsCreated,
    int AssignmentsPreserved,
    string Message);

public sealed record AffiliateRotationStatus(
    bool IsEnabled,
    int ActiveArticleCount,
    int EligibleOfferCount,
    int AssignedArticleCount,
    DateTimeOffset? NextRotationAt);
