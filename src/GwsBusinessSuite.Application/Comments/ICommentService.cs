namespace GwsBusinessSuite.Application.Comments;

public interface ICommentService
{
    Task<CommentView> SubmitAsync(
        Guid articleId,
        string authorName,
        string authorEmail,
        string body,
        Guid? parentCommentId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommentView>> ListApprovedForArticleAsync(Guid articleId, CancellationToken cancellationToken = default);

    // statusFilter is one of CommentStatuses, or null for every status.
    Task<IReadOnlyList<CommentView>> ListForModerationAsync(string? statusFilter, CancellationToken cancellationToken = default);

    Task ApproveAsync(Guid commentId, CancellationToken cancellationToken = default);

    Task MarkSpamAsync(Guid commentId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid commentId, CancellationToken cancellationToken = default);

    Task<int> CountPendingAsync(CancellationToken cancellationToken = default);
}
