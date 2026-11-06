using System.Text;

namespace GwsBusinessSuite.WorldNewsKit;

// One source in an event's document: the first report of a story, plus the other outlets that
// carried the same wire copy (the same description, or the same headline when there's none).
public sealed record DocumentSource(WorldArticle Report, IReadOnlyList<string> AlsoCarriedBy);

// The combined document for one event (the plan's "single document"), read in the app and
// downloaded as a PDF. Since 06-11-26 it answers, in this order, what Grant asked of it: the claim
// the coverage makes, what the outlets report, who they are, what each leaves out, what they agree
// on, and then every source with its link. The claim, the summary, the gaps and the agreement are
// SentinelGPT's (EventAnalysis, written when the document is read, from the outlets' own articles
// where they could be read); who reports it, the figures and the sources are the reports themselves,
// with no AI. Times go through formatTime so the host shows them its own way (dd-MM-yy HH:mm in GWS).
public static class EventDocument
{
    public const string ClaimHeading = "The claim";
    public const string OverviewHeading = "What the outlets report";
    public const string OutletsHeading = "Who is reporting it";
    public const string GapsHeading = "What each outlet leaves out or adds";
    public const string AgreedHeading = "What they agree on";
    public const string SourcesHeading = "Sources";
    // Before 06-11-26 (the summary model's own comparison points).
    public const string ComparisonHeading = "How the coverage compares";

    public static IReadOnlyList<DocumentSource> Sources(IEnumerable<WorldArticle> articles) =>
        articles.OrderBy(a => a.SeenAt).ThenBy(a => a.Outlet)
            .GroupBy(CopyKey)
            .Select(copies => new DocumentSource(copies.First(),
                copies.Skip(1).Select(a => a.Outlet).Where(o => o != copies.First().Outlet).Distinct().ToList()))
            .OrderBy(s => s.Report.SeenAt).ThenBy(s => s.Report.Outlet)
            .ToList();

    public static string ToMarkdown(EventOverview e, IReadOnlyList<WorldArticle> articles, EventSummary? summary,
        Func<DateTimeOffset, string> formatTime, EventAnalysis? analysis = null)
    {
        var md = new StringBuilder();
        md.AppendLine($"# {Inline(e.Headline ?? e.Title)}").AppendLine();
        if (e.Headline is not null) md.AppendLine($"Original headline: {Inline(e.Title)}").AppendLine();
        md.AppendLine($"- Outlets: {e.OutletCount}, reports: {e.ArticleCount}");
        md.AppendLine($"- Languages: {string.Join(", ", e.Languages.Order())}");
        md.AppendLine($"- Reported from: {string.Join(", ", e.Countries.Order())}");
        md.AppendLine($"- First seen {formatTime(e.FirstSeen)}, latest {formatTime(e.LastSeen)}").AppendLine();

        const string waiting = "_SentinelGPT hasn't analysed this coverage yet. It does when the document is opened in the app while your Mac app is open._";

        md.AppendLine($"## {ClaimHeading}").AppendLine();
        md.AppendLine(analysis is { Claim.Length: > 0 } ? Inline(analysis.Claim) : waiting).AppendLine();

        md.AppendLine($"## {OverviewHeading}").AppendLine();
        if (analysis is { Summary.Length: > 0 }) md.AppendLine(Inline(analysis.Summary)).AppendLine();
        else if (summary is not null) md.AppendLine(Inline(summary.Text)).AppendLine().AppendLine("_A short summary from the headlines alone._").AppendLine();
        else md.AppendLine("_No summary yet._").AppendLine();

        md.AppendLine($"## {OutletsHeading}").AppendLine();
        AppendCoverage(md, articles, formatTime);

        md.AppendLine($"## {GapsHeading}").AppendLine();
        if (analysis is not null)
        {
            var comparisons = EventAnalyzer.Compare(analysis);
            if (comparisons.Count == 0 && analysis.Differences.Count == 0)
                md.AppendLine("_SentinelGPT found no outlet whose coverage stands out from the rest._").AppendLine();
            foreach (var comparison in comparisons)
            {
                var parts = new List<string>();
                if (comparison.LeavesOut.Count > 0) parts.Add("Leaves out: " + string.Join(" ", comparison.LeavesOut.Select(Inline)));
                if (comparison.Adds is { } adds) parts.Add("Adds: " + Inline(adds));
                md.AppendLine($"- **{Inline(comparison.Outlet)}:** {string.Join(" ", parts)}");
            }
            foreach (var general in analysis.Differences.Where(d => d.Outlet.Length == 0)) md.AppendLine($"- {Inline(general.Text)}");
            md.AppendLine();
            if (comparisons.Any(c => c.LeavesOut.Count > 0))
                md.AppendLine("_\"Leaves out\" lists the agreed facts below that an outlet's report doesn't state, going by the outlets SentinelGPT names for each fact._").AppendLine();
        }
        else if (summary?.Comparison is { Count: > 0 } points)
        {
            foreach (var point in points) md.AppendLine($"- {Inline(point)}");
            md.AppendLine();
        }
        else md.AppendLine(waiting).AppendLine();

        md.AppendLine($"## {AgreedHeading}").AppendLine();
        if (analysis is { Agreed.Count: > 0 })
        {
            foreach (var fact in analysis.Agreed)
                md.AppendLine(fact.Outlets.Count == 0 ? $"- {Inline(fact.Text)}"
                    : $"- {Inline(fact.Text)} ({Count(fact.Outlets.Count, "outlet")}: {Outlets(fact.Outlets)})");
            md.AppendLine();
        }
        else if (analysis is null) md.AppendLine(waiting).AppendLine();
        AppendFigures(md, CoverageClaims.For(articles));

        if (analysis is not null)
        {
            md.AppendLine($"_The claim, the summary, what each outlet leaves out or adds, and what they agree on were written by SentinelGPT ({Inline(analysis.Model)}), " +
                $"{formatTime(analysis.CreatedAt)}, from the headlines and descriptions of {Count(analysis.ArticleCount, "report")}" +
                (analysis.ArticlesRead > 0 ? $" and {Count(analysis.ArticlesRead, "article")} read in full" : string.Empty) +
                ". Check the sources below before relying on a detail._").AppendLine();
        }
        else if (summary is not null)
        {
            md.AppendLine($"_The summary was written by AI ({Inline(summary.Model)}) from the headlines and descriptions of {Count(summary.ArticleCount, "report")}, " +
                $"{formatTime(summary.CreatedAt)} (version {summary.Version}). Check the sources below before relying on a detail._").AppendLine();
        }

        md.AppendLine($"## {SourcesHeading}").AppendLine();
        foreach (var source in Sources(articles))
        {
            var a = source.Report;
            md.AppendLine($"### {Inline(a.Outlet)}: {a.Country}, {a.Language}, {formatTime(a.SeenAt)}").AppendLine();
            md.AppendLine($"[{Inline(a.Title)}]({a.Url})").AppendLine();
            if (a.Snippet is not null) md.AppendLine($"> {Inline(a.Snippet)}").AppendLine();
            if (source.AlsoCarriedBy.Count > 0) md.AppendLine($"Also carried by: {string.Join(", ", source.AlsoCarriedBy)}").AppendLine();
        }
        return md.ToString();
    }

    // The figures the reports give, read with plain patterns (CoverageClaims), each with the outlets behind it.
    private static void AppendFigures(StringBuilder md, IReadOnlyList<CoverageClaim> claims)
    {
        if (claims.Count == 0) return;
        md.AppendLine("**Figures in the reports** (read from each headline and description by fixed rules, without AI):").AppendLine();
        foreach (var claim in claims)
        {
            var values = string.Join("; ", claim.Values.Take(6).Select(v => $"{ValueText(v)} ({Outlets(v.Sources.Select(s => s.Article.Outlet))})"));
            var verdict = claim.Corroborated ? " The coverage disagrees."
                : claim.Disputed ? " The figures vary."
                : claim.Consensus ? $" {Count(claim.Values[0].Outlets, "outlet")} agree."
                : string.Empty;
            if (claim.MayHaveRisen) verdict += " The lower figures all came first, so the number may have risen.";
            md.AppendLine($"- {claim.Label}: {values}.{verdict}");
        }
        md.AppendLine();
    }

    // Each language's outlets and where they're based, biggest first, then who reported first and last.
    private static void AppendCoverage(StringBuilder md, IReadOnlyList<WorldArticle> articles, Func<DateTimeOffset, string> formatTime)
    {
        if (articles.Count == 0) return;
        foreach (var language in articles.GroupBy(a => a.Language)
                     .OrderByDescending(g => g.Select(a => a.Outlet).Distinct().Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var outlets = language.Select(a => a.Outlet).Distinct().ToList();
            md.AppendLine($"- {language.Key}: {Count(outlets.Count, "outlet")} ({string.Join(", ", outlets.Select(Inline))}), " +
                $"from {string.Join(", ", language.Select(a => a.Country).Distinct().Order(StringComparer.Ordinal))}");
        }
        var first = articles.MinBy(a => a.SeenAt)!;
        var latest = articles.MaxBy(a => a.SeenAt)!;
        md.AppendLine($"- First report: {Inline(first.Outlet)}, {formatTime(first.SeenAt)}. Latest: {Inline(latest.Outlet)}, {formatTime(latest.SeenAt)}.")
            .AppendLine();
    }

    public static string ValueText(ClaimValue value) => value.Qualifier switch
    {
        ClaimQualifier.AtLeast => $"at least {Number(value.Value)}",
        ClaimQualifier.About => $"about {Number(value.Value)}",
        ClaimQualifier.UpTo => $"up to {Number(value.Value)}",
        _ => Number(value.Value)
    };

    private static string Number(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Outlets(IEnumerable<string> outlets)
    {
        var names = outlets.Distinct().Select(Inline).ToList();
        return names.Count <= 5 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(5))} and {names.Count - 5} more";
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    // The sources as the summary model reads them, one line each, within maxChars. When they don't
    // all fit, languages take turns (biggest first), so every language's coverage is heard; the
    // chosen lines then go back into time order.
    public static string ToPrompt(EventOverview e, IReadOnlyList<WorldArticle> articles, Func<DateTimeOffset, string> formatTime,
        int maxChars, int maxSnippet = 280)
    {
        var header = $"Event: {e.Title}\nCovered by {e.OutletCount} outlets in {string.Join(", ", e.Languages.Order())}, " +
            $"{formatTime(e.FirstSeen)} to {formatTime(e.LastSeen)}.\n\nReports:\n";
        var queues = Sources(articles)
            .GroupBy(s => s.Report.Language)
            .OrderByDescending(g => g.Count())
            .Select(g => new Queue<DocumentSource>(g.OrderBy(s => s.Report.Snippet is null).ThenBy(s => s.Report.SeenAt)))
            .ToList();
        var chosen = new List<(DocumentSource Source, string Line)>();
        var budget = maxChars - header.Length;
        while (queues.Any(q => q.Count > 0))
        {
            foreach (var queue in queues.Where(q => q.Count > 0))
            {
                var source = queue.Dequeue();
                var line = Line(source, formatTime, maxSnippet);
                if (line.Length + 1 > budget) continue;
                chosen.Add((source, line));
                budget -= line.Length + 1;
            }
        }
        return header + string.Join('\n', chosen.OrderBy(c => c.Source.Report.SeenAt).Select(c => c.Line));
    }

    private static string Line(DocumentSource source, Func<DateTimeOffset, string> formatTime, int maxSnippet)
    {
        var a = source.Report;
        var line = $"- {a.Outlet} ({a.Country}, {a.Language}, {formatTime(a.SeenAt)}): {a.Title}";
        if (a.Snippet is not null) line += " | " + (a.Snippet.Length <= maxSnippet ? a.Snippet : a.Snippet[..maxSnippet].TrimEnd() + "…");
        if (source.AlsoCarriedBy.Count > 0) line += $" [also carried by {string.Join(", ", source.AlsoCarriedBy)}]";
        return line;
    }

    // Wire copy: the same description (long enough to mean something), else the same headline.
    private static string CopyKey(WorldArticle a) =>
        a.Snippet is { Length: >= 80 } snippet ? "s:" + Letters(snippet) : "t:" + Letters(a.Title);

    private static string Letters(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Keeps a feed's text from breaking the Markdown around it.
    private static string Inline(string text) =>
        text.Replace('\n', ' ').Replace("[", "\\[").Replace("]", "\\]");
}
