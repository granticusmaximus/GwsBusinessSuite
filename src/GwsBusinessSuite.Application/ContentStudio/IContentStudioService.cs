namespace GwsBusinessSuite.Application.ContentStudio;

public interface IContentStudioService
{
    Task<IReadOnlyList<ContentStudioDraftSummary>> ListDraftsAsync(CancellationToken cancellationToken = default);
    Task<int> CountPendingReviewAsync(CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult> GenerateArticleAsync(
        ArticleGenerationRequest request,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> RequestRevisionAsync(
        DraftRevisionRequest request,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> GenerateHeroImageAsync(
        DraftHeroImageGenerationRequest request,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> UploadHeroImageAsync(
        DraftHeroImageUploadRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContentStudioRevisionView>> GetRevisionHistoryAsync(
        Guid draftId,
        CancellationToken cancellationToken = default);
    Task<ContentStudioRevisionDiff?> GetRevisionDiffAsync(
        Guid draftId,
        Guid revisionId,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> RestoreRevisionAsync(
        DraftRevisionRestoreRequest request,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> UpdateDraftMarkdownAsync(
        DraftMarkdownUpdateRequest request,
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
    Task RecordAffiliatePlacementInteractionAsync(
        AffiliatePlacementInteractionRequest request,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default);
}
