namespace GwsBusinessSuite.Application.Wiki;

public sealed record SentinelReactionView(
    string Emoji,
    int Count,
    bool ReactedByCurrentUser);

public sealed record SentinelDiscussionCommentView(
    Guid Id,
    Guid? ParentCommentId,
    string Body,
    string Author,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SentinelReactionView> Reactions);

public sealed record SentinelDiscussionView(
    Guid Id,
    Guid WikiPageId,
    Guid? BlockId,
    bool IsResolved,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SentinelDiscussionCommentView> Comments);

public sealed record SentinelNotificationView(
    Guid Id,
    string Kind,
    Guid WikiPageId,
    Guid? DiscussionId,
    string Message,
    DateTimeOffset CreatedAt,
    bool IsRead);

public interface ISentinelCollaborationService
{
    Task<IReadOnlyList<SentinelDiscussionView>> ListDiscussionsAsync(
        Guid wikiPageId,
        string currentUsername,
        bool includeResolved = false,
        CancellationToken cancellationToken = default);

    Task<SentinelDiscussionView> CreateDiscussionAsync(
        Guid wikiPageId,
        Guid? blockId,
        string body,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task ReplyAsync(
        Guid discussionId,
        Guid? parentCommentId,
        string body,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task SetResolvedAsync(
        Guid discussionId,
        bool resolved,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task ToggleReactionAsync(
        Guid commentId,
        string emoji,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelNotificationView>> ListNotificationsAsync(
        string username,
        bool unreadOnly = false,
        int maxResults = 20,
        CancellationToken cancellationToken = default);

    Task MarkNotificationReadAsync(
        Guid notificationId,
        string username,
        CancellationToken cancellationToken = default);

    Task MarkAllNotificationsReadAsync(
        string username,
        CancellationToken cancellationToken = default);
}
