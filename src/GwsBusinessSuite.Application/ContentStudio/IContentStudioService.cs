using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Application.ContentStudio;

public interface IContentStudioService
{
    Task<IReadOnlyList<ContentStudioDraftSummary>> ListDraftsAsync(CancellationToken cancellationToken = default);
    Task<int> CountPendingReviewAsync(CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult> GenerateArticleAsync(
        ArticleGenerationRequest request,
        CancellationToken cancellationToken = default);
    // Same generation as GenerateArticleAsync, but yields markdown fragments as Ollama streams
    // them so the UI can show the article being written rather than a blank wait for the whole
    // multi-minute CPU-only generation - see ContentStudioGenerationChunk.
    IAsyncEnumerable<ContentStudioGenerationChunk> GenerateArticleStreamAsync(
        ArticleGenerationRequest request,
        CancellationToken cancellationToken = default);
    Task<ArticleGenerationResult?> RequestRevisionAsync(
        DraftRevisionRequest request,
        CancellationToken cancellationToken = default);

    // Same generation/revision pipelines as above (prompt, compile-and-repair, affiliate slots,
    // persistence), but every model call goes to modelRuntime instead of this server's own Ollama.
    // Content Studio passes a BrowserLocalOllamaService here so the heavy generation runs on the
    // Ollama of the machine the browser is on, while the server only builds prompts and saves.
    IAsyncEnumerable<ContentStudioGenerationChunk> GenerateArticleStreamAsync(
        ArticleGenerationRequest request,
        IOllamaService modelRuntime,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This Content Studio implementation does not support a caller-supplied model runtime.");
    Task<ArticleGenerationResult?> RequestRevisionAsync(
        DraftRevisionRequest request,
        IOllamaService modelRuntime,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This Content Studio implementation does not support a caller-supplied model runtime.");
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
