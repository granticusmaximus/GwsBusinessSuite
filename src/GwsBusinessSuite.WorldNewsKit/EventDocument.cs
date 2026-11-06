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

    // The story, once, for the screen and the Markdown: outlets with where they're based and whether
    // their article was read in full, the sources, the figures, English headlines and what changed.
    public static StoryDocument Build(EventOverview e, IReadOnlyList<WorldArticle> articles, EventSummary? summary, EventAnalysis? analysis,
        IReadOnlyDictionary<string, string>? english = null, IReadOnlySet<string>? readInFull = null, StoryChanges? changes = null)
    {
        var outlets = articles.GroupBy(a => a.Outlet)
            .Select(outlet => new StoryOutlet(outlet.Key,
                outlet.GroupBy(a => a.Language).OrderByDescending(language => language.Count()).First().Key,
                outlet.GroupBy(a => a.Country).OrderByDescending(country => country.Count()).First().Key,
                outlet.Count(), outlet.Min(a => a.SeenAt), readInFull is not null && outlet.Any(a => readInFull.Contains(a.Id))))
            .OrderByDescending(outlet => outlet.Reports)
            .ThenBy(outlet => outlet.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new StoryDocument(e, summary, analysis, outlets, Sources(articles), CoverageClaims.For(articles),
            english ?? new Dictionary<string, string>(), changes,
            articles.MinBy(a => a.SeenAt), articles.MaxBy(a => a.SeenAt));
    }

    public static string ToMarkdown(EventOverview e, IReadOnlyList<WorldArticle> articles, EventSummary? summary,
        Func<DateTimeOffset, string> formatTime, EventAnalysis? analysis = null) =>
        ToMarkdown(Build(e, articles, summary, analysis), formatTime);

    public const string Waiting = "SentinelGPT hasn't analysed this coverage yet. It does when the document is opened in the app while your Mac app is open.";

    public static string ToMarkdown(StoryDocument story, Func<DateTimeOffset, string> formatTime)
    {
        var e = story.Event;
        var analysis = story.Analysis;
        var summary = story.Summary;
        var md = new StringBuilder();
        md.AppendLine($"# {Inline(e.Headline ?? e.Title)}").AppendLine();
        if (e.Headline is not null) md.AppendLine($"Original headline: {Inline(e.Title)}").AppendLine();
        md.AppendLine($"- Outlets: {e.OutletCount}, reports: {e.ArticleCount}");
        md.AppendLine($"- Languages: {string.Join(", ", e.Languages.Order())}");
        md.AppendLine($"- Reported from: {string.Join(", ", e.Countries.Order())}");
        md.AppendLine($"- First seen {formatTime(e.FirstSeen)}, latest {formatTime(e.LastSeen)}").AppendLine();

        if (story.Changes is { Any: true } changes)
        {
            md.AppendLine($"## {ChangesHeading} ({formatTime(changes.Since)})").AppendLine();
            foreach (var line in ChangeLines(changes)) md.AppendLine($"- {Inline(line)}");
            md.AppendLine();
        }

        var waiting = $"_{Waiting}_";
        md.AppendLine($"## {ClaimHeading}").AppendLine();
        md.AppendLine(analysis is { Claim.Length: > 0 } ? Inline(analysis.Claim) : waiting).AppendLine();

        md.AppendLine($"## {OverviewHeading}").AppendLine();
        if (analysis is { Summary.Length: > 0 }) md.AppendLine(Inline(analysis.Summary)).AppendLine();
        else if (summary is not null) md.AppendLine(Inline(summary.Text)).AppendLine().AppendLine("_A short summary from the headlines alone._").AppendLine();
        else md.AppendLine("_No summary yet._").AppendLine();

        md.AppendLine($"## {OutletsHeading}").AppendLine();
        AppendCoverage(md, story, formatTime);

        md.AppendLine($"## {GapsHeading}").AppendLine();
        if (analysis is not null)
        {
            var comparisons = story.Comparisons;
            if (comparisons.Count == 0 && analysis.Differences.Count == 0)
                md.AppendLine("_SentinelGPT found no outlet whose coverage stands out from the rest._").AppendLine();
            foreach (var comparison in comparisons) md.AppendLine($"- **{Inline(comparison.Outlet)}:** {Inline(ComparisonText(comparison))}");
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
            var number = 0;
            foreach (var fact in analysis.Agreed)
                md.AppendLine(fact.Outlets.Count == 0 ? $"{++number}. {Inline(fact.Text)}"
                    : $"{++number}. {Inline(fact.Text)} ({Count(fact.Outlets.Count, "outlet")}: {Outlets(fact.Outlets)})");
            md.AppendLine();
            AppendGrid(md, story);
        }
        else if (analysis is null) md.AppendLine(waiting).AppendLine();
        AppendFigures(md, story.Figures);

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
        foreach (var source in story.Sources)
        {
            var a = source.Report;
            md.AppendLine($"### {Inline(a.Outlet)}: {a.Country}, {a.Language}, {formatTime(a.SeenAt)}").AppendLine();
            md.AppendLine($"[{Inline(a.Title)}]({a.Url})").AppendLine();
            if (story.English.TryGetValue(a.Id, out var english)) md.AppendLine($"_In English: {Inline(english)}_").AppendLine();
            if (a.Snippet is not null) md.AppendLine($"> {Inline(a.Snippet)}").AppendLine();
            if (source.AlsoCarriedBy.Count > 0) md.AppendLine($"Also carried by: {string.Join(", ", source.AlsoCarriedBy)}").AppendLine();
        }
        return md.ToString();
    }

    public const string ChangesHeading = "What changed since you last read";
    public const string GridHeading = "Who reports what";
    public const string Stated = "●";
    public const string NotStated = "–";

    // "Leaves out: … Adds: …" for one outlet.
    public static string ComparisonText(OutletComparison comparison)
    {
        var parts = new List<string>();
        if (comparison.LeavesOut.Count > 0) parts.Add("Leaves out: " + string.Join(" ", comparison.LeavesOut));
        if (comparison.Adds is { } adds) parts.Add("Adds: " + adds);
        return string.Join(" ", parts);
    }

    public static IReadOnlyList<string> ChangeLines(StoryChanges changes)
    {
        var lines = new List<string>();
        if (changes.NewOutlets.Count > 0) lines.Add($"{Count(changes.NewOutlets.Count, "new outlet")}: {Outlets(changes.NewOutlets)}");
        if (changes.PreviousClaim is { } claim) lines.Add($"The claim changed. It was: {claim}");
        lines.AddRange(changes.NewFacts.Select(fact => $"Now agreed: {fact}"));
        lines.AddRange(changes.DroppedFacts.Select(fact => $"No longer among the agreed facts: {fact}"));
        lines.AddRange(changes.FigureChanges.Select(figure => $"Figures: {figure}"));
        return lines;
    }

    // Who reports what: a row per outlet, a column per agreed fact (numbered as in the list above).
    private static void AppendGrid(StringBuilder md, StoryDocument story)
    {
        var facts = story.GridFacts;
        var rows = story.Grid;
        if (facts.Count == 0 || rows.Count == 0) return;
        var numbers = story.Analysis!.Agreed.Select((fact, index) => (fact, index)).Where(x => x.fact.Outlets.Count > 0).Select(x => x.index + 1).ToList();
        md.AppendLine($"**{GridHeading}** ({Stated} the outlet's report states the fact, {NotStated} it doesn't):").AppendLine();
        md.AppendLine("| Outlet | " + string.Join(" | ", numbers) + " |");
        md.AppendLine("| --- | " + string.Join(" | ", numbers.Select(_ => ":-:")) + " |");
        foreach (var row in rows)
            md.AppendLine($"| {Cell(row.Outlet)} | " + string.Join(" | ", row.States.Select(state => state ? Stated : NotStated)) + " |");
        md.AppendLine();
    }

    private static string Cell(string text) => Inline(text).Replace("|", "\\|");

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
    private static void AppendCoverage(StringBuilder md, StoryDocument story, Func<DateTimeOffset, string> formatTime)
    {
        if (story.Outlets.Count == 0) return;
        foreach (var language in story.Outlets.GroupBy(outlet => outlet.Language)
                     .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            md.AppendLine($"- {language.Key}: {Count(language.Count(), "outlet")} ({string.Join(", ", language.Select(outlet => Inline(outlet.Name)))}), " +
                $"from {string.Join(", ", language.Select(outlet => outlet.Country).Distinct().Order(StringComparer.Ordinal))}");
        }
        if (story.FirstReport is { } first && story.LatestReport is { } latest)
            md.AppendLine($"- First report: {Inline(first.Outlet)}, {formatTime(first.SeenAt)}. Latest: {Inline(latest.Outlet)}, {formatTime(latest.SeenAt)}.");
        md.AppendLine();
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
