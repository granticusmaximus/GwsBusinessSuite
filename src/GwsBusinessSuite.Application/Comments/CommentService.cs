using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Comments;

public sealed class CommentService(
    IAppDbContext dbContext,
    ICurrentUserAccessor? currentUserAccessor = null) : ICommentService
{
    private const int MaxAuthorNameLength = 100;
    private const int MaxAuthorEmailLength = 200;
    private const int MaxBodyLength = 5000;
    private readonly ICurrentUserAccessor _currentUserAccessor = currentUserAccessor ?? FixedCurrentUserAccessor.Unknown;

    public async Task<CommentView> SubmitAsync(
        Guid articleId,
        string authorName,
        string authorEmail,
        string body,
        Guid? parentCommentId = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = (authorName ?? string.Empty).Trim();
        var trimmedEmail = (authorEmail ?? string.Empty).Trim();
        var trimmedBody = (body ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new ArgumentException("Name is required.", nameof(authorName));
        }

        if (string.IsNullOrWhiteSpace(trimmedEmail) || !trimmedEmail.Contains('@'))
        {
            throw new ArgumentException("A valid email is required.", nameof(authorEmail));
        }

        if (string.IsNullOrWhiteSpace(trimmedBody))
        {
            throw new ArgumentException("Comment cannot be empty.", nameof(body));
        }

        var articleExists = await dbContext.Articles.AnyAsync(a => a.Id == articleId, cancellationToken);
        if (!articleExists)
        {
            throw new InvalidOperationException("The article this comment belongs to no longer exists.");
        }

        if (parentCommentId is { } requestedParentId)
        {
            var parent = await dbContext.Comments
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == requestedParentId && c.ArticleId == articleId, cancellationToken);

            if (parent is null)
            {
                throw new InvalidOperationException("The comment you're replying to no longer exists.");
            }

            if (parent.Status != CommentStatuses.Approved)
            {
                throw new InvalidOperationException("Replies can only target approved comments.");
            }
        }

        var comment = new Comment
        {
            ArticleId = articleId,
            ParentCommentId = parentCommentId,
            AuthorName = Truncate(trimmedName, MaxAuthorNameLength),
            AuthorEmail = Truncate(trimmedEmail, MaxAuthorEmailLength),
            Body = Truncate(trimmedBody, MaxBodyLength),
            Status = CommentStatuses.Pending,
            CreatedBy = "public-comment"
        };

        await dbContext.Comments.AddAsync(comment, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToView(comment, articleTitle: string.Empty, articleSlug: string.Empty);
    }

    public async Task<IReadOnlyList<CommentView>> ListApprovedForArticleAsync(Guid articleId, CancellationToken cancellationToken = default)
    {
        var comments = await dbContext.Comments
            .AsNoTracking()
            .Where(c => c.ArticleId == articleId && c.Status == CommentStatuses.Approved)
            .ToListAsync(cancellationToken);

        return BuildCommentTree(
            comments,
            _ => (string.Empty, string.Empty),
            rootOrderDescending: false);
    }

    public async Task<IReadOnlyList<CommentView>> ListForModerationAsync(string? statusFilter, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Comments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            query = query.Where(c => c.Status == statusFilter);
        }

        var comments = await query.ToListAsync(cancellationToken);
        var articlesById = await dbContext.Articles
            .AsNoTracking()
            .Where(a => comments.Select(c => c.ArticleId).Contains(a.Id))
            .Select(a => new { a.Id, a.Title, a.Slug })
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        return FlattenComments(
            BuildCommentTree(
                comments,
                comment =>
                {
                    var article = articlesById.GetValueOrDefault(comment.ArticleId);
                    return (article?.Title ?? "(deleted article)", article?.Slug ?? string.Empty);
                },
                rootOrderDescending: true))
            .ToList();
    }

    public async Task ApproveAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        await SetStatusAsync(commentId, CommentStatuses.Approved, cancellationToken);
    }

    public async Task MarkSpamAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        await SetStatusAsync(commentId, CommentStatuses.Spam, cancellationToken);
    }

    public async Task MarkPendingAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        await SetStatusAsync(commentId, CommentStatuses.Pending, cancellationToken);
    }

    public async Task DeleteAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        var comment = await dbContext.Comments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return;
        }

        var replies = await dbContext.Comments
            .Where(child => child.ParentCommentId == commentId)
            .ToListAsync(cancellationToken);

        if (replies.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var performedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
            foreach (var reply in replies)
            {
                reply.ParentCommentId = comment.ParentCommentId;
                reply.UpdatedAt = now;
                reply.UpdatedBy = performedBy;
            }
        }

        dbContext.Comments.Remove(comment);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.Comments.CountAsync(c => c.Status == CommentStatuses.Pending, cancellationToken);
    }

    private async Task SetStatusAsync(Guid commentId, string status, CancellationToken cancellationToken)
    {
        var comment = await dbContext.Comments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return;
        }

        comment.Status = status;
        comment.UpdatedAt = DateTimeOffset.UtcNow;
        comment.UpdatedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;

    private static IReadOnlyList<CommentView> BuildCommentTree(
        IReadOnlyList<Comment> comments,
        Func<Comment, (string ArticleTitle, string ArticleSlug)> articleLookup,
        bool rootOrderDescending)
    {
        var commentsById = comments.ToDictionary(comment => comment.Id);
        var effectiveParents = comments.ToDictionary(
            comment => comment.Id,
            comment => comment.ParentCommentId is { } parentId && commentsById.ContainsKey(parentId) ? comment.ParentCommentId : null);
        var childrenByParent = comments.ToLookup(comment => effectiveParents[comment.Id]);

        CommentView BuildView(Comment comment, int depth)
        {
            var (articleTitle, articleSlug) = articleLookup(comment);
            var parentAuthor = comment.ParentCommentId is { } parentId && commentsById.TryGetValue(parentId, out var parent)
                ? parent.AuthorName
                : string.Empty;

            return new CommentView
            {
                Id = comment.Id,
                ArticleId = comment.ArticleId,
                ParentCommentId = effectiveParents[comment.Id],
                ArticleTitle = articleTitle,
                ArticleSlug = articleSlug,
                ParentAuthorName = parentAuthor,
                AuthorName = comment.AuthorName,
                AuthorEmail = comment.AuthorEmail,
                Body = comment.Body,
                Status = comment.Status,
                CreatedAt = comment.CreatedAt,
                Depth = depth,
                Replies = childrenByParent[comment.Id]
                    .OrderBy(child => child.CreatedAt)
                    .Select(child => BuildView(child, depth + 1))
                    .ToList()
            };
        }

        var roots = childrenByParent[null].AsEnumerable();
        roots = rootOrderDescending
            ? roots.OrderByDescending(comment => comment.CreatedAt)
            : roots.OrderBy(comment => comment.CreatedAt);

        return roots
            .Select(comment => BuildView(comment, depth: 0))
            .ToList();
    }

    private static IEnumerable<CommentView> FlattenComments(IEnumerable<CommentView> comments)
    {
        foreach (var comment in comments)
        {
            yield return comment;

            foreach (var reply in FlattenComments(comment.Replies))
            {
                yield return reply;
            }
        }
    }

    private static CommentView ToView(Comment comment, string articleTitle, string articleSlug) => new()
    {
        Id = comment.Id,
        ArticleId = comment.ArticleId,
        ParentCommentId = comment.ParentCommentId,
        ArticleTitle = articleTitle,
        ArticleSlug = articleSlug,
        ParentAuthorName = string.Empty,
        AuthorName = comment.AuthorName,
        AuthorEmail = comment.AuthorEmail,
        Body = comment.Body,
        Status = comment.Status,
        CreatedAt = comment.CreatedAt,
        Depth = 0
    };
}
