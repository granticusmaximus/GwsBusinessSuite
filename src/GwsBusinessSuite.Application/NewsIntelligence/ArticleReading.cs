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
}
