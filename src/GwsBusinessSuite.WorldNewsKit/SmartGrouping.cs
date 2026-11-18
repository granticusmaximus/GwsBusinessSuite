using System.Text.Json;

namespace GwsBusinessSuite.WorldNewsKit;

// One search a Pull latest runs, and why it was chosen (shown in the pull's summary).
public sealed record PullQuery(string Query, string Why);

// Pull latest's searches (18-11-26): stories that are surging, stories only one outlet has
// reported (to find a second source) and the person's own watches, taken in turn so no kind
// crowds out the others.
public static class PullPlanner
{
    public const string Surging = "surging";
    public const string Thin = "needs a second source";
    public const string Watch = "your watch";

    public static IReadOnlyList<PullQuery> Plan(IEnumerable<string> surgingTitles, IEnumerable<string> thinTitles, IEnumerable<string> watches, int max)
    {
        var lanes = new[]
        {
            thinTitles.Select(FollowUpQueries.From).OfType<string>().Select(q => new PullQuery(q, Thin)).ToList(),
            surgingTitles.Select(FollowUpQueries.From).OfType<string>().Select(q => new PullQuery(q, Surging)).ToList(),
            watches.Select(w => w.Trim()).Where(w => w.Length >= 3).Select(q => new PullQuery(q, Watch)).ToList(),
        };
        var plan = new List<PullQuery>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var round = 0; plan.Count < max && lanes.Any(lane => round < lane.Count); round++)
            foreach (var lane in lanes)
                if (round < lane.Count && plan.Count < max && seen.Add(lane[round].Query))
                    plan.Add(lane[round]);
        return plan;
    }
}

// Regroup's second opinion (18-11-26): the model reads two stories' headlines and says whether
// they report the same happening. Only asked about close calls, and its answer is kept.
public static class GroupingAdjudicator
{
    public const string SystemPrompt = """
        You compare two groups of news headlines. Decide whether both groups report the same specific event
        (the same incident, announcement, decision or match), not merely the same topic, place or person.
        A follow-up on the same incident (a rising toll, a reaction, an arrest) counts as the same event.
        Answer with JSON only: {"same": true or false, "reason": "one short sentence"}.
        """;

    public static string Prompt(IReadOnlyList<string> headlinesA, IReadOnlyList<string> headlinesB) =>
        "Group A:\n" + List(headlinesA) + "\n\nGroup B:\n" + List(headlinesB);

    private static string List(IReadOnlyList<string> headlines) =>
        string.Join('\n', headlines.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).Select(h => "- " + h.Trim()));

    // Same is null when the reply can't be read.
    public static (bool? Same, string Reason) Parse(string? reply)
    {
        var text = reply?.Trim() ?? "";
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var json = JsonDocument.Parse(text[start..(end + 1)]);
                var root = json.RootElement;
                var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()!.Trim() : "";
                if (root.TryGetProperty("same", out var same))
                {
                    if (same.ValueKind is JsonValueKind.True or JsonValueKind.False) return (same.GetBoolean(), reason);
                    if (same.ValueKind == JsonValueKind.String && bool.TryParse(same.GetString(), out var parsed)) return (parsed, reason);
                }
            }
            catch (JsonException)
            {
            }
        }
        if (text.StartsWith("yes", StringComparison.OrdinalIgnoreCase)) return (true, text[3..].Trim(' ', '.', ',', ':', '-'));
        if (text.StartsWith("no", StringComparison.OrdinalIgnoreCase)) return (false, text[2..].Trim(' ', '.', ',', ':', '-'));
        return (null, "");
    }
}

// Reports that may not belong in a story (18-11-26): headlines naming only countries the rest of
// the story doesn't, offered on the story page as a split for a person to confirm.
public sealed record SplitSuggestion(IReadOnlyList<string> ArticleIds, string Reason);

public static class SplitSuggestions
{
    public static IReadOnlyList<SplitSuggestion> For(IReadOnlyList<WorldArticle> reports)
    {
        var named = reports
            .Select(r => (Report: r, Countries: HeadlineCountries.In(r.English?.Headline ?? (HeadlineTranslator.IsEnglish(r.Language) ? r.Title : ""))))
            .Where(x => x.Countries.Count > 0)
            .ToList();
        if (named.Count < 3) return [];
        var dominant = named.SelectMany(x => x.Countries).GroupBy(c => c)
            .Where(g => g.Count() * 2 >= named.Count).Select(g => g.Key).ToHashSet();
        if (dominant.Count == 0) return [];
        var outliers = named.Where(x => !x.Countries.Overlaps(dominant)).ToList();
        if (outliers.Count == 0 || outliers.Count * 2 >= named.Count) return [];
        var about = string.Join(" and ", dominant.Order(StringComparer.Ordinal));
        return outliers
            .GroupBy(x => string.Join(" and ", x.Countries.Order(StringComparer.Ordinal)))
            .Select(g => new SplitSuggestion(g.Select(x => x.Report.Id).ToList(),
                $"{(g.Count() == 1 ? "1 report names" : $"{g.Count()} reports name")} {g.Key}; the rest of the story is about {about}."))
            .ToList();
    }
}
