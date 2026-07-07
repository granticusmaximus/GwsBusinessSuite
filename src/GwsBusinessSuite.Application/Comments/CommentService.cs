using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Comments;

public sealed class CommentService(IAppDbContext dbContext) : ICommentService
{
    private const int MaxAuthorNameLength = 100;
    private const int MaxAuthorEmailLength = 200;
    private const int MaxBodyLength = 5000;

    public async Task<CommentView> SubmitAsync(
        Guid articleId,
        string authorName,
        string authorEmail,
        string body,
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

        var comment = new Comment
        {
            ArticleId = articleId,
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

        // SQLite can't translate ORDER BY on a DateTimeOffset column, so order
        // client-side after materializing (same pattern used elsewhere in this codebase).
        return comments
            .OrderBy(c => c.CreatedAt)
            .Select(c => ToView(c, articleTitle: string.Empty, articleSlug: string.Empty))
            .ToList();
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

        return comments
            .OrderByDescending(c => c.CreatedAt)
            .Select(c =>
            {
                var article = articlesById.GetValueOrDefault(c.ArticleId);
                return ToView(c, article?.Title ?? "(deleted article)", article?.Slug ?? string.Empty);
            })
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

    public async Task DeleteAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        var comment = await dbContext.Comments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            return;
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
        comment.UpdatedBy = "admin";
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;

    private static CommentView ToView(Comment comment, string articleTitle, string articleSlug) => new()
    {
        Id = comment.Id,
        ArticleId = comment.ArticleId,
        ArticleTitle = articleTitle,
        ArticleSlug = articleSlug,
        AuthorName = comment.AuthorName,
        AuthorEmail = comment.AuthorEmail,
        Body = comment.Body,
        Status = comment.Status,
        CreatedAt = comment.CreatedAt
    };
}
