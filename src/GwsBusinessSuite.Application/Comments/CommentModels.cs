namespace GwsBusinessSuite.Application.Comments;

public sealed class CommentView
{
    public Guid Id { get; init; }
    public Guid ArticleId { get; init; }

    // Only populated for the admin moderation queue - the public comment list already
    // lives on the article's own page, so it has no need to know the article's identity.
    public string ArticleTitle { get; init; } = string.Empty;
    public string ArticleSlug { get; init; } = string.Empty;

    public string AuthorName { get; init; } = string.Empty;
    public string AuthorEmail { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}
