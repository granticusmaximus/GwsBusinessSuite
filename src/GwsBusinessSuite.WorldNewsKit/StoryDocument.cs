using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// One outlet covering a story: where it's based, how many reports it filed, when it first did, and
// whether one of its articles was read in full for the analysis. Language is the one it filed in
// most (the BBC files in many).
public sealed record StoryOutlet(string Name, string Language, string Country, int Reports, DateTimeOffset FirstSeen, bool ReadInFull)
{
    public string Region => WorldRegions.Of(Country);
    public bool IsWestern => WorldRegions.IsWestern(Country);
}

// What changed since a reader last read the story: outlets that joined, agreed facts that are new
// or no longer agreed, the claim it replaced (when it changed), and figures that moved.
public sealed record StoryChanges(DateTimeOffset Since, IReadOnlyList<string> NewOutlets, IReadOnlyList<string> NewFacts,
    IReadOnlyList<string> DroppedFacts, string? PreviousClaim, IReadOnlyList<string> FigureChanges)
{
    public bool Any => NewOutlets.Count > 0 || NewFacts.Count > 0 || DroppedFacts.Count > 0 || PreviousClaim is not null || FigureChanges.Count > 0;
}

// A row of the who-reports-what grid: whether the outlet's report states each agreed fact.
public sealed record StoryGridRow(string Outlet, IReadOnlyList<bool> States);

// Everything the story's document shows, built once (EventDocument.Build) and drawn both on screen
// and as Markdown for the PDF, so the two can't drift apart (06-11-26).
public sealed record StoryDocument(
    EventOverview Event,
    EventSummary? Summary,
    EventAnalysis? Analysis,
    IReadOnlyList<StoryOutlet> Outlets,
    IReadOnlyList<DocumentSource> Sources,
    IReadOnlyList<CoverageClaim> Figures,
    // Article id -> English headline, for reports not in English.
    IReadOnlyDictionary<string, string> English,
    StoryChanges? Changes,
    WorldArticle? FirstReport = null,
    WorldArticle? LatestReport = null,
    StoryTrustSummary? Trust = null)
{
    public IReadOnlyList<OutletComparison> Comparisons => Analysis is null ? [] : EventAnalyzer.Compare(Analysis);

    // The grid's columns: the agreed facts that name the outlets stating them.
    public IReadOnlyList<AgreedFact> GridFacts => Analysis?.Agreed.Where(fact => fact.Outlets.Count > 0).ToList() ?? [];

    // The grid's rows: every outlet named for a fact or for what it adds, those stating the most first.
    public IReadOnlyList<StoryGridRow> Grid
    {
        get
        {
            var facts = GridFacts;
            if (facts.Count == 0 || Analysis is null) return [];
            return facts.SelectMany(fact => fact.Outlets)
                .Concat(Analysis.Differences.Where(d => d.Outlet.Length > 0).Select(d => d.Outlet))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(outlet => new StoryGridRow(outlet, facts.Select(fact => fact.Outlets.Contains(outlet, StringComparer.OrdinalIgnoreCase)).ToList()))
                .OrderByDescending(row => row.States.Count(state => state))
                .ThenBy(row => row.Outlet, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public StoryOutlet? Outlet(string name) => Outlets.FirstOrDefault(outlet => outlet.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public static partial class StoryChangesFinder
{
    // Two facts or claims are the same when most of their words are.
    public const double SameText = 0.5;

    public static StoryChanges? Find(EventAnalysis current, EventAnalysis? previous, DateTimeOffset since, IReadOnlyList<WorldArticle> articles)
    {
        if (previous is null || previous.CreatedAt >= current.CreatedAt) return null;
        var newOutlets = articles.GroupBy(a => a.Outlet)
            .Where(outlet => outlet.Min(a => a.SeenAt) > since)
            .OrderBy(outlet => outlet.Min(a => a.SeenAt))
            .Select(outlet => outlet.Key)
            .ToList();
        var newFacts = current.Agreed.Where(fact => previous.Agreed.All(before => Similarity(before.Text, fact.Text) < SameText)).Select(fact => fact.Text).ToList();
        var dropped = previous.Agreed.Where(fact => current.Agreed.All(now => Similarity(now.Text, fact.Text) < SameText)).Select(fact => fact.Text).ToList();
        var claim = previous.Claim.Length > 0 && Similarity(previous.Claim, current.Claim) < SameText ? previous.Claim : null;

        var before = CoverageClaims.For(articles.Where(a => a.SeenAt <= since).ToList());
        var figures = new List<string>();
        foreach (var now in CoverageClaims.For(articles))
        {
            var then = before.FirstOrDefault(earlier => earlier.Measure == now.Measure && earlier.Label == now.Label);
            var nowText = Values(now);
            if (then is null) figures.Add($"{now.Label}: {nowText} (new)");
            else if (Values(then) is var thenText && thenText != nowText) figures.Add($"{now.Label}: {thenText} → {nowText}");
        }
        return new StoryChanges(since, newOutlets, newFacts, dropped, claim, figures);
    }

    private static string Values(CoverageClaim claim) =>
        string.Join("; ", claim.Values.OrderByDescending(value => value.Outlets).Take(3).Select(EventDocument.ValueText));

    public static double Similarity(string a, string b)
    {
        var left = Words(a);
        var right = Words(b);
        if (left.Count == 0 || right.Count == 0) return 0;
        return (double)left.Intersect(right).Count() / left.Union(right).Count();
    }

    private static HashSet<string> Words(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(match => match.Value).Where(word => word.Length >= 3).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
