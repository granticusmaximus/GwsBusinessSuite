namespace GwsBusinessSuite.WorldNewsKit;

// An Overwatch item (a conflict event, a hazard, a warning) opened in World News (19-11-26, Grant:
// "what the report is, the executive summary, the actual report details and then what others are
// reporting"). The report comes from the Overwatch window; the coverage from World News search.
public sealed record OverwatchReport(
    string Kind,
    string KindLabel,
    string Title,
    string Detail,
    string? Severity,
    string? AsOf,
    string? SourceUrl,
    string Query,
    int Days);

// A World News story found for the report, with its latest summary when one has been written.
public sealed record BriefCoverage(FoundStory Story, string? Summary);

public static class OverwatchBrief
{
    public const int MaxStoriesInPrompt = 5;
    private const int MaxDetail = 2_000;
    private const int MaxSummary = 600;

    public const string SystemPrompt =
        "You write an executive summary for an analyst. You get one report from a monitoring feed (a conflict " +
        "event, a natural hazard or a warning) and the news stories found about it. In 3 to 5 plain sentences: " +
        "say what happened, where and when, and how serious it is, from the report; then say what the news " +
        "coverage adds or whether it differs. Use only what you are given. If no story is clearly about the same " +
        "event, say the news coverage doesn't confirm it yet. No headings, no lists, no markdown.";

    public static string Prompt(OverwatchReport report, IReadOnlyList<BriefCoverage> coverage)
    {
        var lines = new List<string>
        {
            $"REPORT ({report.KindLabel}{(string.IsNullOrWhiteSpace(report.Severity) ? "" : $", severity {report.Severity}")}" +
                $"{(string.IsNullOrWhiteSpace(report.AsOf) ? "" : $", as of {report.AsOf}")}): {report.Title}",
            Limit(report.Detail, MaxDetail),
            "",
            coverage.Count == 0 ? $"NEWS COVERAGE: no World News story found in the last {report.Days} days." : "NEWS COVERAGE:",
        };
        foreach (var (item, i) in coverage.Take(MaxStoriesInPrompt).Select((c, i) => (c, i + 1)))
        {
            var e = item.Story.Event;
            lines.Add($"{i}. {e.Headline ?? e.Title} ({e.OutletCount} outlets, {string.Join(", ", e.Countries.Take(4))}; " +
                      $"{(item.Story.IsStrong ? "close match" : "loose match")})");
            if (!string.IsNullOrWhiteSpace(item.Summary)) lines.Add("   " + Limit(item.Summary!, MaxSummary));
        }
        return string.Join('\n', lines);
    }

    // How far back World News is searched: UCDP runs a month behind, GDELT's reports are from the
    // last day, everything else a week.
    public static int DaysFor(string? kind) => kind switch
    {
        "conflict-confirmed" => 30,
        "conflict" => 3,
        _ => 7,
    };

    // The search words: the window's own (a conflict's sides and place), else its title without the
    // "reported -" and punctuation.
    public static string QueryFor(string title, string? newsQuery) =>
        !string.IsNullOrWhiteSpace(newsQuery) ? newsQuery.Trim()
            : System.Text.RegularExpressions.Regex.Replace(title.Replace(" reported - ", " ", StringComparison.Ordinal), @"[\s\-:()]+", " ").Trim();

    // Only a web link is shown as the official source; anything else (javascript:, data:) isn't.
    public static string? SafeLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;

    // Strong matches first, then a few loose ones, as the page lists them.
    public static IReadOnlyList<FoundStory> Order(IReadOnlyList<FoundStory> stories, int strong = 8, int loose = 4) =>
        stories.Where(s => s.IsStrong).Take(strong).Concat(stories.Where(s => !s.IsStrong).Take(loose)).ToList();

    private static string Limit(string text, int max)
    {
        var flat = text.Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }
}
