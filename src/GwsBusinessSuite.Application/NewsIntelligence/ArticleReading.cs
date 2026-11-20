namespace GwsBusinessSuite.Application.NewsIntelligence;

/// <summary>
/// An article opened in the app's reader (20-11-26, Media Watch): the outlet's own page with its
/// navigation, ads and scripts taken away, as safe HTML. When the page can't be read here (a
/// paywall, a refusal, no readable text), <see cref="Problem"/> says why and Html is empty, so the
/// reader offers the original instead.
/// </summary>
public sealed record ReaderArticle(
    string Url,
    string Title,
    string? Byline,
    string? Site,
    DateTimeOffset? PublishedAt,
    string Html,
    int Minutes,
    string? Problem,
    string Markdown = "",
    string Text = "")
{
    public bool Readable => Problem is null && Html.Length > 0;
}

public interface IArticleReader
{
    Task<ReaderArticle> ReadAsync(string url, string? fallbackTitle = null, CancellationToken cancellationToken = default);

    // Three takeaways and one line on what it means for GWS, from the local model (20-11-26).
    Task<ReaderTakeaways> TakeawaysAsync(string url, CancellationToken cancellationToken = default);

    // Ask the article (20-11-26): an answer from the article's own text, with the passage it rests on.
    Task<ReaderAnswer> AskAsync(string url, string question, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReaderAnswer(string.Empty, null, "Questions aren't available here."));

    // The article translated into English by the AI model, laid out like the original (20-11-26).
    // Not readable (with the reason) when the page or the model couldn't do it.
    Task<ReaderArticle> TranslateAsync(string url, string? fallbackTitle = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReaderArticle(url, fallbackTitle ?? url, null, null, null, string.Empty, 0, "Translation isn't available here."));
}

// An answer from one article: what it says, the passage quoted, or why there's none.
public sealed record ReaderAnswer(string Answer, string? Quote, string? Problem);
