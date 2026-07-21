using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelCollaborationService(IAppDbContext dbContext, TimeProvider timeProvider)
    : ISentinelCollaborationService
{
    private const int MaxBodyLength = 5000;
    private static readonly HashSet<string> AllowedReactions = ["👍", "❤️", "🎉", "👀", "✅"];
    private static readonly Regex MentionPattern = new(
        @"(?<![\p{L}\p{N}_.-])@([\p{L}\p{N}_](?:[\p{L}\p{N}_.-]*[\p{L}\p{N}_])?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<SentinelDiscussionView>> ListDiscussionsAsync(
        Guid wikiPageId,
        string currentUsername,
        bool includeResolved = false,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.SentinelDiscussions.AsNoTracking()
            .Where(discussion => discussion.WikiPageId == wikiPageId);
        if (!includeResolved)
        {
            query = query.Where(discussion => discussion.ResolvedAt == null);
        }

        var discussions = await query
            .Include(discussion => discussion.Comments)
                .ThenInclude(comment => comment.Reactions)
            .ToListAsync(cancellationToken);

        return discussions
            .OrderByDescending(discussion => discussion.CreatedAt)
            .Select(discussion => ToView(discussion, currentUsername))
            .ToList();
    }

    public async Task<SentinelDiscussionView> CreateDiscussionAsync(
        Guid wikiPageId,
        Guid? blockId,
        string body,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedBody = ValidateBody(body);
        var actor = NormalizeUsername(performedBy);
        var page = await dbContext.WikiPages.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken)
            ?? throw new InvalidOperationException("The Sentinel page no longer exists.");
        if (blockId is { } requestedBlockId
            && !WikiBlockJson.ParseBlocks(page.BlocksJson).Any(block => block.Id == requestedBlockId))
        {
            throw new InvalidOperationException("The block this discussion targets no longer exists.");
        }

        var now = timeProvider.GetUtcNow();
        var discussion = new SentinelDiscussion
        {
            WikiPageId = wikiPageId,
            BlockId = blockId,
            CreatedAt = now,
            CreatedBy = actor
        };
        var comment = new SentinelDiscussionComment
        {
            SentinelDiscussionId = discussion.Id,
            Body = normalizedBody,
            CreatedAt = now,
            CreatedBy = actor
        };
        await dbContext.SentinelDiscussions.AddAsync(discussion, cancellationToken);
        await dbContext.SentinelDiscussionComments.AddAsync(comment, cancellationToken);

        await AddNotificationsAsync(
            CandidateRecipients(page.CreatedBy, [], normalizedBody),
            actor,
            page,
            discussion.Id,
            comment.Id,
            "discussion-created",
            $"{actor} started a discussion in {page.Title}: {Preview(normalizedBody)}",
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (await ListDiscussionsAsync(wikiPageId, actor, includeResolved: true, cancellationToken))
            .Single(item => item.Id == discussion.Id);
    }

    public async Task ReplyAsync(
        Guid discussionId,
        Guid? parentCommentId,
        string body,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedBody = ValidateBody(body);
        var actor = NormalizeUsername(performedBy);
        var discussion = await dbContext.SentinelDiscussions
            .Include(item => item.WikiPage)
            .Include(item => item.Comments)
            .FirstOrDefaultAsync(item => item.Id == discussionId, cancellationToken)
            ?? throw new InvalidOperationException("The discussion no longer exists.");
        if (discussion.ResolvedAt.HasValue)
        {
            throw new InvalidOperationException("Reopen the discussion before replying.");
        }
        if (parentCommentId is { } replyTarget
            && discussion.Comments.All(comment => comment.Id != replyTarget))
        {
            throw new InvalidOperationException("The reply target is not part of this discussion.");
        }

        var now = timeProvider.GetUtcNow();
        var comment = new SentinelDiscussionComment
        {
            SentinelDiscussionId = discussion.Id,
            ParentCommentId = parentCommentId,
            Body = normalizedBody,
            CreatedAt = now,
            CreatedBy = actor
        };
        await dbContext.SentinelDiscussionComments.AddAsync(comment, cancellationToken);
        var page = discussion.WikiPage!;
        await AddNotificationsAsync(
            CandidateRecipients(page.CreatedBy, discussion.Comments.Select(item => item.CreatedBy), normalizedBody),
            actor,
            page,
            discussion.Id,
            comment.Id,
            "discussion-reply",
            $"{actor} replied in {page.Title}: {Preview(normalizedBody)}",
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetResolvedAsync(
        Guid discussionId,
        bool resolved,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeUsername(performedBy);
        var discussion = await dbContext.SentinelDiscussions
            .Include(item => item.WikiPage)
            .Include(item => item.Comments)
            .FirstOrDefaultAsync(item => item.Id == discussionId, cancellationToken)
            ?? throw new InvalidOperationException("The discussion no longer exists.");
        var now = timeProvider.GetUtcNow();
        discussion.ResolvedAt = resolved ? now : null;
        discussion.ResolvedBy = resolved ? actor : null;
        discussion.UpdatedAt = now;
        discussion.UpdatedBy = actor;

        var page = discussion.WikiPage!;
        await AddNotificationsAsync(
            CandidateRecipients(page.CreatedBy, discussion.Comments.Select(item => item.CreatedBy), string.Empty),
            actor,
            page,
            discussion.Id,
            null,
            resolved ? "discussion-resolved" : "discussion-reopened",
            $"{actor} {(resolved ? "resolved" : "reopened")} a discussion in {page.Title}.",
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleReactionAsync(
        Guid commentId,
        string emoji,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        if (!AllowedReactions.Contains(emoji))
        {
            throw new ArgumentException("Unsupported reaction.", nameof(emoji));
        }

        var actor = NormalizeUsername(performedBy);
        var comment = await dbContext.SentinelDiscussionComments
            .Include(item => item.Discussion)!
                .ThenInclude(discussion => discussion!.WikiPage)
            .Include(item => item.Reactions)
            .FirstOrDefaultAsync(item => item.Id == commentId, cancellationToken)
            ?? throw new InvalidOperationException("The discussion comment no longer exists.");
        var existing = comment.Reactions.FirstOrDefault(reaction =>
            reaction.Username == actor && reaction.Emoji == emoji);
        if (existing is not null)
        {
            dbContext.SentinelDiscussionReactions.Remove(existing);
        }
        else
        {
            var now = timeProvider.GetUtcNow();
            await dbContext.SentinelDiscussionReactions.AddAsync(new SentinelDiscussionReaction
            {
                SentinelDiscussionCommentId = comment.Id,
                Username = actor,
                Emoji = emoji,
                CreatedAt = now,
                CreatedBy = actor
            }, cancellationToken);
            var page = comment.Discussion!.WikiPage!;
            await AddNotificationsAsync(
                [comment.CreatedBy], actor, page, comment.Discussion.Id, comment.Id,
                "discussion-reaction", $"{actor} reacted {emoji} to your comment in {page.Title}.",
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SentinelNotificationView>> ListNotificationsAsync(
        string username,
        bool unreadOnly = false,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        var query = dbContext.SentinelNotifications.AsNoTracking()
            .Where(notification => notification.Username == normalizedUser);
        if (unreadOnly) query = query.Where(notification => notification.ReadAt == null);
        var notifications = await query.ToListAsync(cancellationToken);
        return notifications
            .OrderByDescending(notification => notification.CreatedAt)
            .Take(Math.Max(0, maxResults))
            .Select(notification => new SentinelNotificationView(
                notification.Id,
                notification.Kind,
                notification.WikiPageId,
                notification.SentinelDiscussionId,
                notification.Message,
                notification.CreatedAt,
                notification.ReadAt.HasValue))
            .ToList();
    }

    public async Task MarkNotificationReadAsync(
        Guid notificationId,
        string username,
        CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        var notification = await dbContext.SentinelNotifications.FirstOrDefaultAsync(item =>
            item.Id == notificationId && item.Username == normalizedUser, cancellationToken);
        if (notification is null || notification.ReadAt.HasValue) return;
        notification.ReadAt = timeProvider.GetUtcNow();
        notification.UpdatedAt = notification.ReadAt;
        notification.UpdatedBy = normalizedUser;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAllNotificationsReadAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        var unread = await dbContext.SentinelNotifications
            .Where(item => item.Username == normalizedUser && item.ReadAt == null)
            .ToListAsync(cancellationToken);
        if (unread.Count == 0) return;
        var now = timeProvider.GetUtcNow();
        foreach (var notification in unread)
        {
            notification.ReadAt = now;
            notification.UpdatedAt = now;
            notification.UpdatedBy = normalizedUser;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task AddNotificationsAsync(
        IEnumerable<string> recipients,
        string actor,
        WikiPage page,
        Guid discussionId,
        Guid? commentId,
        string kind,
        string message,
        CancellationToken cancellationToken)
    {
        var requested = recipients
            .Select(NormalizeUsername)
            .Where(username => username != actor && username != "unknown")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return;

        var activeUsers = await dbContext.AppUsers.AsNoTracking()
            .Where(user => user.IsActive)
            .Select(user => user.Username)
            .ToListAsync(cancellationToken);
        requested.IntersectWith(activeUsers.Select(NormalizeUsername));
        var now = timeProvider.GetUtcNow();
        foreach (var recipient in requested)
        {
            await dbContext.SentinelNotifications.AddAsync(new SentinelNotification
            {
                Username = recipient,
                Kind = kind,
                WikiPageId = page.Id,
                SentinelDiscussionId = discussionId,
                SentinelDiscussionCommentId = commentId,
                Message = message,
                CreatedAt = now,
                CreatedBy = actor
            }, cancellationToken);
        }
    }

    private static IEnumerable<string> CandidateRecipients(
        string pageOwner,
        IEnumerable<string> participants,
        string body) =>
        new[] { pageOwner }
            .Concat(participants)
            .Concat(MentionPattern.Matches(body).Select(match => match.Groups[1].Value));

    private static SentinelDiscussionView ToView(SentinelDiscussion discussion, string currentUsername)
    {
        var normalizedUser = NormalizeUsername(currentUsername);
        return new SentinelDiscussionView(
            discussion.Id,
            discussion.WikiPageId,
            discussion.BlockId,
            discussion.ResolvedAt.HasValue,
            discussion.ResolvedAt,
            discussion.ResolvedBy,
            discussion.CreatedAt,
            discussion.Comments
                .OrderBy(comment => comment.CreatedAt)
                .Select(comment => new SentinelDiscussionCommentView(
                    comment.Id,
                    comment.ParentCommentId,
                    comment.Body,
                    comment.CreatedBy,
                    comment.CreatedAt,
                    comment.Reactions
                        .GroupBy(reaction => reaction.Emoji)
                        .Select(group => new SentinelReactionView(
                            group.Key,
                            group.Count(),
                            group.Any(reaction => reaction.Username == normalizedUser)))
                        .OrderBy(reaction => reaction.Emoji, StringComparer.Ordinal)
                        .ToList()))
                .ToList());
    }

    private static string ValidateBody(string body)
    {
        var normalized = (body ?? string.Empty).Trim();
        if (normalized.Length == 0) throw new ArgumentException("Comment cannot be empty.", nameof(body));
        return normalized.Length <= MaxBodyLength ? normalized : normalized[..MaxBodyLength];
    }

    private static string NormalizeUsername(string username) =>
        string.IsNullOrWhiteSpace(username) ? "unknown" : username.Trim().ToLowerInvariant();

    private static string Preview(string body) => body.Length <= 120 ? body : body[..120] + "…";
}
