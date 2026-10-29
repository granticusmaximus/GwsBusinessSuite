namespace GwsBusinessSuite.WorldNewsKit;

// What SentinelGPT's search_world_news tool gets back: the events that match a question, each with
// its English headline and summary when it has them, its coverage, its situation, and a few sources.
public sealed record WorldNewsSearchResult(string Query, string MatchedBy, IReadOnlyList<WorldNewsSearchItem> Results);

public sealed record WorldNewsSearchItem(string Headline, string? OriginalHeadline, string? Summary, int Outlets, int Reports,
    IReadOnlyList<string> Countries, IReadOnlyList<string> Languages, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    WorldNewsSearchSituation? Situation, IReadOnlyList<WorldNewsSearchSource> Sources);

public sealed record WorldNewsSearchSituation(string Title, bool Active, int Events);

public sealed record WorldNewsSearchSource(string Outlet, string Title, string Url, string Language);

public sealed record WorldNewsCandidate(string EventId, float[] Centroid, string? SituationId);

// Ranks recent events against a question by meaning: the question is embedded with the same model
// as the headlines, so "train crash in Spain" finds the Spanish, Dutch and Hebrew reports alike.
// Measured 29-10-26 on 2,622 events of 2+ reports: questions about real stories scored 0.66-0.89
// against their best match (the Fed decision's tenth-best was still 0.53), while an unrelated one
// ("banana bread recipe") topped out at 0.38. A situation gives at most PerSituation results, so one
// big story doesn't crowd out the rest.
public static class WorldNewsSearch
{
    public const double MinScore = 0.5;
    // Bringing news into an answer unasked needs a clearer match: everyday business questions
    // ("how is the sales pipeline doing", "is anything down") scored 0.38-0.54 against noise
    // headlines, news questions 0.66 and up (measured 29-10-26).
    public const double ContextMinScore = 0.62;
    public const int Limit = 8;
    public const int PerSituation = 3;
    public const int MinArticles = 2;
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public static IReadOnlyList<(string EventId, double Score)> Rank(
        float[] query, IEnumerable<WorldNewsCandidate> candidates, int limit = Limit, double minScore = MinScore, int perSituation = PerSituation)
    {
        var unit = EventClusterer.Normalize(query);
        var perSituationCount = new Dictionary<string, int>();
        var hits = new List<(string, double)>();
        foreach (var (candidate, score) in candidates
                     .Select(c => (Candidate: c, Score: (double)EventClusterer.Dot(EventClusterer.Normalize(c.Centroid), unit)))
                     .Where(c => c.Score >= minScore)
                     .OrderByDescending(c => c.Score))
        {
            if (candidate.SituationId is { } situation)
            {
                var seen = perSituationCount.GetValueOrDefault(situation);
                if (seen >= perSituation) continue;
                perSituationCount[situation] = seen + 1;
            }
            hits.Add((candidate.EventId, score));
            if (hits.Count == limit) break;
        }
        return hits;
    }

    // Question words that would match half of all English headlines.
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "what", "whats", "what's", "about", "with", "from", "that", "this", "are", "was", "were", "has",
        "have", "any", "news", "latest", "happening", "going", "tell", "there", "how", "who", "why", "when", "where", "did"
    };

    // Without the embedding model: events whose headline holds the question's words (3+ letters,
    // question words left out), most words matched first, then the most widely covered.
    public static IReadOnlyList<string> MatchWords(string query, IEnumerable<EventOverview> events, int limit = Limit)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('?', '!', '.', ',', '"', '\'', ':', ';'))
            .Where(word => word.Length >= 3 && !Ignored.Contains(word))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (words.Count == 0) return [];
        return events
            .Select(e => (e.Id, e.OutletCount, Matched: words.Count(word =>
                e.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                || e.Headline?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true)))
            .Where(e => e.Matched > 0)
            .OrderByDescending(e => e.Matched).ThenByDescending(e => e.OutletCount)
            .Take(limit)
            .Select(e => e.Id)
            .ToList();
    }

    // Up to four sources from different outlets, English first, then one per other language.
    public static IReadOnlyList<WorldNewsSearchSource> Sources(IEnumerable<WorldArticle> articles, int count = 4) =>
        articles
            .OrderByDescending(a => a.Language == "English").ThenByDescending(a => a.SeenAt)
            .DistinctBy(a => a.Outlet)
            .GroupBy(a => a.Language)
            .SelectMany(group => group.Take(group.Key == "English" ? 2 : 1))
            .Take(count)
            .Select(a => new WorldNewsSearchSource(a.Outlet, a.Title, a.Url, a.Language))
            .ToList();
}
