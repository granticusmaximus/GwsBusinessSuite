namespace GwsBusinessSuite.Application.ContentStudio;

public interface IContentStudioService
{
    Task<IReadOnlyList<ContentStudioDraftSummary>> ListDraftsAsync(CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult> GenerateArticleAsync(
        ArticleGenerationRequest request,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> RequestRevisionAsync(
        DraftRevisionRequest request,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> ApproveDraftAsync(
        DraftDecisionRequest request,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> PublishDraftToSiteAsync(
        DraftPublishRequest request,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> RejectDraftAsync(
        DraftDecisionRequest request,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> RegenerateHeroImageAsync(
        DraftHeroImageRegenerationRequest request,
        CancellationToken cancellationToken = default);
    Task RecordAffiliatePlacementInteractionAsync(
        AffiliatePlacementInteractionRequest request,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default);
    Task<int> BulkRegenerateHeroImagesAsync(CancellationToken cancellationToken = default);
}
