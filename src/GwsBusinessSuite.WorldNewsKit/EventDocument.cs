using System.Text;

namespace GwsBusinessSuite.WorldNewsKit;

// One source in an event's document: the first report of a story, plus the other outlets that
// carried the same wire copy (the same description, or the same headline when there's none).
public sealed record DocumentSource(WorldArticle Report, IReadOnlyList<string> AlsoCarriedBy);

// The combined document for one event (the plan's "single document"): a header, the summary
// if there is one, then every source in time order with its link and its outlet's description.
// The same sources, trimmed to fit, are what the summary model reads. Times go through
// formatTime so the host shows them its own way (dd-MM-yy HH:mm in GWS).
public static class EventDocument
{
    public static IReadOnlyList<DocumentSource> Sources(IEnumerable<WorldArticle> articles) =>
        articles.OrderBy(a => a.SeenAt).ThenBy(a => a.Outlet)
            .GroupBy(CopyKey)
            .Select(copies => new DocumentSource(copies.First(),
                copies.Skip(1).Select(a => a.Outlet).Where(o => o != copies.First().Outlet).Distinct().ToList()))
            .OrderBy(s => s.Report.SeenAt).ThenBy(s => s.Report.Outlet)
            .ToList();

    public static string ToMarkdown(EventOverview e, IReadOnlyList<WorldArticle> articles, EventSummary? summary,
        Func<DateTimeOffset, string> formatTime)
    {
        var md = new StringBuilder();
        md.AppendLine($"# {Inline(e.Headline ?? e.Title)}").AppendLine();
        if (e.Headline is not null) md.AppendLine($"Original headline: {Inline(e.Title)}").AppendLine();
        md.AppendLine($"- Outlets: {e.OutletCount}, reports: {e.ArticleCount}");
        md.AppendLine($"- Languages: {string.Join(", ", e.Languages.Order())}");
        md.AppendLine($"- Reported from: {string.Join(", ", e.Countries.Order())}");
        md.AppendLine($"- First seen {formatTime(e.FirstSeen)}, latest {formatTime(e.LastSeen)}").AppendLine();

        if (summary is not null)
        {
            md.AppendLine("## Summary").AppendLine().AppendLine(summary.Text).AppendLine();
            md.AppendLine($"_Written by {summary.Model} from {summary.ArticleCount} reports, {formatTime(summary.CreatedAt)} (version {summary.Version})._").AppendLine();
        }

        md.AppendLine("## Sources").AppendLine();
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
