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

public sealed record SentinelDiscussionAnchor(
    string Text,
    int Start,
    int End);

public sealed record SentinelDiscussionView(
    Guid Id,
    Guid WikiPageId,
    Guid? BlockId,
    bool IsResolved,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SentinelDiscussionCommentView> Comments,
    SentinelDiscussionAnchor? Anchor = null);

// Client-side facet filtering (Phase 5.3) over an already-loaded discussion list
// (SentinelDiscussions.razor) - a thread has no single "Author" field of its own (it's a
// container of comments), so "the thread's author" is defined here as whoever started it.
public static class SentinelDiscussionFiltering
{
    public static string ThreadAuthor(SentinelDiscussionView discussion) =>
        discussion.Comments.Count > 0 ? discussion.Comments[0].Author : string.Empty;

    public static IReadOnlyList<SentinelDiscussionView> Apply(
        IReadOnlyList<SentinelDiscussionView> discussions,
        string? authorFilter,
        string? dateFilter,
        DateTimeOffset now)
    {
        IEnumerable<SentinelDiscussionView> filtered = discussions;
        if (!string.IsNullOrEmpty(authorFilter))
        {
            filtered = filtered.Where(discussion => string.Equals(ThreadAuthor(discussion), authorFilter, StringComparison.OrdinalIgnoreCase));
        }

        var cutoff = dateFilter switch
        {
            SentinelSearchFiltering.PastWeek => now.AddDays(-7),
            SentinelSearchFiltering.PastMonth => now.AddMonths(-1),
            SentinelSearchFiltering.PastYear => now.AddYears(-1),
            _ => (DateTimeOffset?)null
        };
        if (cutoff is { } cutoffValue)
        {
            filtered = filtered.Where(discussion => discussion.CreatedAt >= cutoffValue);
        }

        return filtered.ToList();
    }
}

public sealed record SentinelNotificationView(
    Guid Id,
    string Kind,
    Guid WikiPageId,
    Guid? DiscussionId,
    string Message,
    DateTimeOffset CreatedAt,
    bool IsRead);

// A block-relative character range for an active (unresolved) discussion's anchor - used to
// render an inline highlight over the exact commented text, not just a per-block pin icon.
public sealed record SentinelDiscussionHighlight(Guid DiscussionId, int Start, int End);

public static class SentinelDiscussionSummary
{
    public static IReadOnlyDictionary<Guid, int> OpenBlockCounts(
        IEnumerable<SentinelDiscussionView> discussions) => discussions
        .Where(discussion => !discussion.IsResolved && discussion.BlockId.HasValue)
        .GroupBy(discussion => discussion.BlockId!.Value)
        .ToDictionary(group => group.Key, group => group.Count());

    public static IReadOnlyDictionary<Guid, IReadOnlyList<SentinelDiscussionHighlight>> OpenBlockHighlights(
        IEnumerable<SentinelDiscussionView> discussions) => discussions
        .Where(discussion => !discussion.IsResolved && discussion.BlockId.HasValue && discussion.Anchor is not null)
        .GroupBy(discussion => discussion.BlockId!.Value)
        .ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<SentinelDiscussionHighlight>)group
                .Select(discussion => new SentinelDiscussionHighlight(discussion.Id, discussion.Anchor!.Start, discussion.Anchor.End))
                .ToList());
}

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
        SentinelDiscussionAnchor? anchor = null,
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
