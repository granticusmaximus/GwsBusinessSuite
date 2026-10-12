using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// Workspace insights: tags (item 21), page analytics (item 22) and knowledge gaps (item 23).

public sealed record SentinelTagCount(string Tag, int Pages);
public sealed record SentinelTaggedPage(Guid Id, string Title, string? Icon, IReadOnlyList<string> Tags);
public sealed record SentinelPageTagsView(IReadOnlyList<string> TextTags, IReadOnlyList<string> ManualTags);
public sealed record SentinelPageViewStats(int Views, int Viewers, int Days);
public sealed record SentinelReadPage(Guid Id, string Title, string? Icon, int Views, int Viewers, DateTimeOffset? LastViewedAt);
public sealed record SentinelKnowledgeGap(string Topic, IReadOnlyList<string> Questions, IReadOnlyList<Guid> QuestionIds, int Asks, DateTimeOffset LastAskedAt);

public interface ISentinelInsightsService
{
    // Tags (item 21). Lists respect View access.
    Task<IReadOnlyList<SentinelTagCount>> ListTagsAsync(string username, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelTaggedPage>> ListTaggedPagesAsync(string tag, string username, CancellationToken cancellationToken = default);
    Task<SentinelPageTagsView> GetPageTagsAsync(Guid pageId, CancellationToken cancellationToken = default);
    Task<SentinelPageTagsView> AddManualTagAsync(Guid pageId, string tag, string username, CancellationToken cancellationToken = default);
    Task<SentinelPageTagsView> RemoveManualTagAsync(Guid pageId, string tag, string username, CancellationToken cancellationToken = default);

    // Analytics (item 22).
    Task RecordViewAsync(Guid pageId, string username, CancellationToken cancellationToken = default);
    Task<SentinelPageViewStats> GetViewStatsAsync(Guid pageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelReadPage>> ListMostReadAsync(string username, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelReadPage>> ListNeverReadAsync(string username, CancellationToken cancellationToken = default);

    // Knowledge gaps (item 23). Admins see everyone's questions; others only their own.
    Task<IReadOnlyList<SentinelKnowledgeGap>> ListKnowledgeGapsAsync(string username, CancellationToken cancellationToken = default);
    // Creates the page for a gap (its questions as the brief), marks them resolved, returns its id.
    Task<Guid> WriteGapPageAsync(SentinelKnowledgeGap gap, string username, CancellationToken cancellationToken = default);
}

public static partial class SentinelTags
{
    public const int MaxLength = 80;

    // "#Client/Acme," -> "client/acme"; null for anything that isn't a usable tag.
    public static string? Normalize(string? raw)
    {
        var tag = (raw ?? string.Empty).Trim().TrimStart('#').Trim('/').ToLowerInvariant();
        tag = MultiSlash().Replace(tag, "/");
        return tag.Length is 0 or > MaxLength || !TagShape().IsMatch(tag) ? null : tag;
    }

    // #tags written in a page's text blocks (not code, and not inside links).
    public static IReadOnlyList<string> FromBlocks(IReadOnlyList<WikiBlock> blocks) =>
        blocks.Where(b => b.Type is not (WikiBlockTypes.Code or WikiBlockTypes.Diagram or WikiBlockTypes.Equation))
            .SelectMany(b => b.RichText.Where(s => string.IsNullOrEmpty(s.Link) && !s.Code))
            .SelectMany(s => InlineTag().Matches(s.Text).Select(m => Normalize(m.Groups[1].Value)))
            .OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();

    // A page tagged "client/acme" is also under "client".
    public static bool IsUnder(string pageTag, string tag) =>
        pageTag == tag || pageTag.StartsWith(tag + "/", StringComparison.Ordinal);

    // "#client" or "#client/acme" terms in a search query, and the rest of the query.
    public static (IReadOnlyList<string> Tags, string Remainder) SplitQuery(string query)
    {
        var tags = InlineTag().Matches(query ?? string.Empty).Select(m => Normalize(m.Groups[1].Value)).OfType<string>().Distinct().ToList();
        var rest = InlineTag().Replace(query ?? string.Empty, " ").Trim();
        return (tags, MultiSpace().Replace(rest, " "));
    }

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}_\-]*(?:/[\p{L}\p{N}_\-]+)*$")]
    private static partial Regex TagShape();

    // Starts a word, begins with a letter (so "#1" and "C#" aren't tags).
    [GeneratedRegex(@"(?<![\p{L}\p{N}_&/#])#(\p{L}[\p{L}\p{N}_\-]*(?:/[\p{L}\p{N}_\-]+)*)")]
    private static partial Regex InlineTag();

    [GeneratedRegex("/{2,}")]
    private static partial Regex MultiSlash();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();
}

public static class SentinelGaps
{
    // A view is counted at most once per user per page in this window.
    public static readonly TimeSpan ViewDebounce = TimeSpan.FromMinutes(30);
    public const int StatsDays = 30;
    // Pages younger than this aren't "never read" yet.
    public const int NeverReadAfterDays = 90;

    public sealed record GapQuestion(Guid Id, string Question, IReadOnlyList<string> Terms, DateTimeOffset AskedAt);

    // Groups unanswered questions that share a key word; the topic is the words most of the group
    // share. Greedy and deterministic, newest first.
    public static IReadOnlyList<SentinelKnowledgeGap> Group(IEnumerable<GapQuestion> questions)
    {
        var groups = new List<List<GapQuestion>>();
        foreach (var question in questions.OrderByDescending(q => q.AskedAt))
        {
            var home = groups.FirstOrDefault(g => g.Any(member => member.Terms.Intersect(question.Terms, StringComparer.OrdinalIgnoreCase).Any()));
            if (home is null) groups.Add([question]);
            else home.Add(question);
        }
        return groups.Select(g =>
            {
                var topicTerms = g.SelectMany(q => q.Terms.Select(t => t.ToLowerInvariant()).Distinct())
                    .GroupBy(t => t).OrderByDescending(t => t.Count()).ThenBy(t => t.Key, StringComparer.Ordinal)
                    .Take(2).Select(t => t.Key).ToList();
                var topic = topicTerms.Count == 0 ? g[0].Question : string.Join(' ', topicTerms);
                return new SentinelKnowledgeGap(char.ToUpperInvariant(topic[0]) + topic[1..],
                    g.Select(q => q.Question).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
                    g.Select(q => q.Id).ToList(), g.Count, g.Max(q => q.AskedAt));
            })
            .OrderByDescending(g => g.Asks).ThenByDescending(g => g.LastAskedAt)
            .ToList();
    }
}
