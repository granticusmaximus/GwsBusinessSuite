using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Articles;

// Published articles that need a refresh (19-11-26): views falling hard against the 30 days before,
// or a year since they were last touched. A refreshed article often earns more than a new one.
public sealed record RefreshCandidate(Guid Id, string Title, string Slug, int ViewsLast30, int ViewsPrior30, DateTimeOffset PublishedAt, string Reason);

public static class ArticleRefresh
{
    public const double FallShare = 0.4;
    public const int MinimumPriorViews = 20;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(365);
    public const int MaxCandidates = 15;
    public const int MaxBodyInPrompt = 4_000;

    public static string PathFor(string slug) => "/blog/" + slug;

    public static IReadOnlyList<RefreshCandidate> Find(IReadOnlyList<Article> articles, IReadOnlyDictionary<string, int> last30,
        IReadOnlyDictionary<string, int> prior30, DateTimeOffset now)
    {
        var candidates = new List<(RefreshCandidate Candidate, double Rank)>();
        foreach (var article in articles.Where(a => a.Status == ArticleStatuses.Published && a.PublishedAt is not null))
        {
            var path = PathFor(article.Slug);
            var recent = last30.GetValueOrDefault(path);
            var before = prior30.GetValueOrDefault(path);
            var touched = article.UpdatedAt is { } updated && updated > article.PublishedAt!.Value ? updated : article.PublishedAt!.Value;
            string? reason = null;
            var rank = 0.0;
            if (before >= MinimumPriorViews && recent <= before * (1 - FallShare))
            {
                var fall = 1 - (double)recent / before;
                reason = $"Views fell {GwsBusinessSuite.Application.Abstractions.GwsMoney.Percent(fall)}: {recent:N0} in the last 30 days against {before:N0} the 30 before.";
                rank = 1 + fall;
            }
            else if (now - touched >= StaleAfter)
            {
                reason = $"Not updated for {(int)((now - touched).TotalDays / 30)} months; facts and links may be out of date.";
                rank = (now - touched).TotalDays / 3650;
            }
            if (reason is not null)
                candidates.Add((new RefreshCandidate(article.Id, article.Title, article.Slug, recent, before, article.PublishedAt!.Value, reason), rank));
        }
        return candidates.OrderByDescending(c => c.Rank).Take(MaxCandidates).Select(c => c.Candidate).ToList();
    }

    public const string SystemPrompt =
        "You help an author refresh an older blog article. From the article given, list 3 to 6 specific, practical changes: " +
        "facts, numbers or versions that are likely out of date, sections worth expanding, a clearer title or description, and " +
        "links worth checking. Say what to change and why, briefly. Don't rewrite the article. Plain text, one change per line, " +
        "each starting with \"- \".";

    public static string Prompt(Article article, DateTimeOffset now)
    {
        var body = article.BodyMarkdown.Length <= MaxBodyInPrompt ? article.BodyMarkdown : article.BodyMarkdown[..MaxBodyInPrompt] + "\n[...]";
        return $"Today is {now:yyyy-MM-dd}. Published {article.PublishedAt:yyyy-MM-dd}.\nTitle: {article.Title}\n" +
               $"Description: {article.MetaDescription}\nKeyword: {article.PrimaryKeyword}\n\n{body}";
    }
}
