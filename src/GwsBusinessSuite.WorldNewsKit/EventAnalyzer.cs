using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// SentinelGPT's reading of one event's coverage, for the top of its document (06-11-26). Grant wanted
// the document to give "what the hypothesis is being stated from the report; a summary of what all
// outlets that are reporting on the matter; who those outlets are; compare and contrast what is
// missing from each outlet; compare and contrast what all are saying is accurate; then a list of the
// links". It's written when someone reads the document, from each outlet's headline and description
// and, where an outlet's own page could be read, an excerpt of the article itself.
public sealed record EventAnalysis(
    string EventId,
    string Claim,
    string Summary,
    IReadOnlyList<AgreedFact> Agreed,
    // What only one outlet reports, or where it disagrees ("adds").
    IReadOnlyList<OutletDifference> Differences,
    string Model,
    // How many reports the event had, and how many articles were read in full, when it was written.
    int ArticleCount,
    int ArticlesRead,
    DateTimeOffset CreatedAt);

// What one outlet adds or disputes. Outlet is empty for a point that names none.
public sealed record OutletDifference(string Outlet, string Text);

// A fact most of the coverage agrees on, and the outlets whose reports state it. What an outlet
// "leaves out" is worked out from these (EventAnalyzer.LeftOut): a small model lists who reports a
// fact far more reliably than it notices what's missing (06-11-26 live check: asked for omissions,
// it only ever wrote what each outlet adds).
public sealed record AgreedFact(string Text, IReadOnlyList<string> Outlets);

// One outlet's place in the comparison: the agreed facts its report doesn't state, and what it adds.
public sealed record OutletComparison(string Outlet, IReadOnlyList<string> LeavesOut, string? Adds);

public static partial class EventAnalyzer
{
    // Characters of reports in the prompt: about 8,000 tokens of Latin script, well inside the
    // 16,384-token context the Mac's model is asked for, with room for the answer.
    public const int MaxPromptChars = 30_000;
    public const int MaxExcerptChars = 1_200;
    public const int MaxAgreed = 8;
    public const int MaxDifferences = 14;
    // An analysis is written again once coverage has grown by a quarter.
    public const double GrowthBeforeRewrite = 1.25;

    public const string SystemPrompt = """
        You are SentinelGPT, analysing the news coverage of one event for a reader who wants to compare the sources.
        You get each outlet's headline and description and, for some outlets, an excerpt of the article itself. The outlets write in many languages; answer in English.
        Rules:
        - Use only what these reports say. Add no outside knowledge, and never guess a number, name, date or motive.
        - Name outlets exactly as they are given.
        - Where outlets disagree, say so rather than picking a side.
        Answer in exactly this form:
        CLAIM: the central claim or hypothesis the coverage puts forward, in 1 or 2 sentences, and whether the reports present it as confirmed, alleged or disputed.
        SUMMARY: 3 to 6 sentences on what the outlets report, taken together.
        AGREED: 3 to 6 lines, one key fact each in a few words, which the most outlets report alike. Each line in this form, listing in the brackets every outlet whose report states that fact:
        - <the fact> [Outlet A, Outlet B, Outlet C]
        DIFFERENCES: one line for each outlet whose coverage stands out, in the form "- Outlet name: <what only it reports, or where it disagrees with the others>". Skip outlets that only repeat the others.
        """;

    // True when there's no analysis yet, or coverage has grown enough since to write it again.
    public static bool IsDue(EventAnalysis? analysis, EventOverview e) =>
        analysis is null || e.ArticleCount >= analysis.ArticleCount * GrowthBeforeRewrite;

    // The reports as SentinelGPT reads them: one per outlet. The articles that could be read come
    // first, an excerpt each; then a line for every other outlet's report, languages taking turns
    // (biggest first) so every language is heard when they don't all fit.
    public static string BuildPrompt(EventOverview e, IReadOnlyList<WorldArticle> articles, IReadOnlyDictionary<string, string> excerpts,
        Func<DateTimeOffset, string> formatTime, int maxChars = MaxPromptChars)
    {
        var text = new StringBuilder();
        text.AppendLine($"Event: {e.Headline ?? e.Title}");
        text.AppendLine($"Covered by {e.OutletCount} outlets in {string.Join(", ", e.Languages.Order(StringComparer.Ordinal))}, " +
            $"{formatTime(e.FirstSeen)} to {formatTime(e.LastSeen)}.");
        text.AppendLine();
        var perOutlet = EventDocument.Sources(articles)
            .GroupBy(source => source.Report.Outlet)
            .Select(group => group
                .OrderByDescending(source => excerpts.ContainsKey(source.Report.Id))
                .ThenByDescending(source => source.Report.Snippet is not null)
                .ThenBy(source => source.Report.SeenAt)
                .First())
            .ToList();

        var read = perOutlet.Where(source => excerpts.ContainsKey(source.Report.Id)).OrderBy(source => source.Report.SeenAt).ToList();
        if (read.Count > 0)
        {
            text.AppendLine("Articles (the opening of each):");
            foreach (var source in read)
            {
                var a = source.Report;
                var block = $"### {a.Outlet} ({a.Country}, {a.Language}, {formatTime(a.SeenAt)})\nHeadline: {Flat(a.Title)}\n" +
                    (a.Snippet is { } snippet ? $"Description: {Clip(snippet, 300)}\n" : string.Empty) +
                    $"Article: {Clip(excerpts[a.Id], MaxExcerptChars)}\n";
                // The other outlets get at least a third of the room.
                if (text.Length + block.Length > maxChars * 2 / 3) break;
                text.AppendLine(block);
            }
        }

        var queues = perOutlet.Where(source => !excerpts.ContainsKey(source.Report.Id))
            .GroupBy(source => source.Report.Language)
            .OrderByDescending(group => group.Count())
            .Select(group => new Queue<DocumentSource>(group.OrderBy(source => source.Report.Snippet is null).ThenBy(source => source.Report.SeenAt)))
            .ToList();
        if (queues.Count > 0)
        {
            text.AppendLine("Other outlets' reports (outlet, country, language: headline | description):");
            var chosen = new List<(DocumentSource Source, string Line)>();
            var budget = maxChars - text.Length;
            while (queues.Any(queue => queue.Count > 0))
            {
                foreach (var queue in queues.Where(queue => queue.Count > 0))
                {
                    var a = queue.Dequeue().Report;
                    var line = $"- {a.Outlet} ({a.Country}, {a.Language}): {Flat(a.Title)}" + (a.Snippet is { } snippet ? $" | {Clip(snippet, 220)}" : string.Empty);
                    if (line.Length + 1 > budget) continue;
                    chosen.Add((new DocumentSource(a, []), line));
                    budget -= line.Length + 1;
                }
            }
            foreach (var (_, line) in chosen.OrderBy(item => item.Source.Report.SeenAt)) text.AppendLine(line);
        }
        return text.ToString().TrimEnd();
    }

    // The four parts of the answer, tolerating markdown bold, headings and a reasoning model's <think>
    // block. A difference is kept with its outlet only when that outlet is one of the event's (a
    // small model can put a point on the wrong outlet); otherwise it's kept as a general point.
    // Null when there's neither a claim nor a summary.
    public static (string Claim, string Summary, IReadOnlyList<AgreedFact> Agreed, IReadOnlyList<OutletDifference> Differences)? Parse(
        string raw, IReadOnlyCollection<string> outlets)
    {
        var text = Think().Replace(raw ?? string.Empty, string.Empty).Replace("**", string.Empty);
        var labels = Label().Matches(text).Cast<Match>().ToList();
        string Section(string name)
        {
            var at = labels.FindIndex(match => match.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return string.Empty;
            var start = labels[at].Index + labels[at].Length;
            var end = at + 1 < labels.Count ? labels[at + 1].Index : text.Length;
            return text[start..end];
        }
        var claim = Flat(Bullet().Replace(Section("CLAIM").Trim(), string.Empty));
        var summary = Flat(Section("SUMMARY"));
        if (claim.Length < 20 && summary.Length < 40) return null;
        var agreed = Bullets(Section("AGREED")).Select(line => Fact(line, outlets)).Where(fact => fact.Text.Length >= 8).Take(MaxAgreed).ToList();
        var differences = Bullets(Section("DIFFERENCES"))
            .Select(line => Difference(line, outlets))
            .Where(difference => difference.Text.Length >= 8)
            .Take(MaxDifferences)
            .ToList();
        return (claim, summary, agreed, differences);
    }

    // The outlets an agreed fact names, and what an outlet leaves out (agreed facts its report
    // doesn't state, among those at least MinOutletsForLeftOut outlets report). Only outlets the
    // analysis looked at count: one named for a fact or for what it adds.
    public const int MinOutletsForLeftOut = 2;

    public static IReadOnlyList<OutletComparison> Compare(EventAnalysis analysis)
    {
        var adds = analysis.Differences.Where(d => d.Outlet.Length > 0).GroupBy(d => d.Outlet, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(d => d.Text)), StringComparer.OrdinalIgnoreCase);
        var main = analysis.Agreed.Where(fact => fact.Outlets.Count >= MinOutletsForLeftOut).ToList();
        var outlets = adds.Keys.Concat(analysis.Agreed.SelectMany(fact => fact.Outlets)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return outlets
            .Select(outlet => new OutletComparison(outlet,
                main.Where(fact => !fact.Outlets.Contains(outlet, StringComparer.OrdinalIgnoreCase)).Select(fact => fact.Text).ToList(),
                adds.GetValueOrDefault(outlet)))
            .Where(comparison => comparison.LeavesOut.Count > 0 || comparison.Adds is not null)
            .OrderByDescending(comparison => comparison.Adds is not null)
            .ThenByDescending(comparison => comparison.LeavesOut.Count)
            .ThenBy(comparison => comparison.Outlet, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AgreedFact Fact(string line, IReadOnlyCollection<string> outlets)
    {
        var match = Bracketed().Match(line);
        if (!match.Success) return new AgreedFact(line, []);
        var named = match.Groups[2].Value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => Known(name, outlets)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new AgreedFact(match.Groups[1].Value.Trim().TrimEnd('.', ',', ';', ':').Trim() + ".", named);
    }

    private static string? Known(string named, IReadOnlyCollection<string> outlets)
    {
        named = named.Trim().Trim('"', '\'', '*', '_').Trim();
        return outlets.FirstOrDefault(known => known.Equals(named, StringComparison.OrdinalIgnoreCase))
            ?? outlets.Where(known => known.Length >= 3 && (named.Contains(known, StringComparison.OrdinalIgnoreCase)
                    || (named.Length >= 3 && known.Contains(named, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(known => known.Length).FirstOrDefault();
    }

    private static OutletDifference Difference(string line, IReadOnlyCollection<string> outlets)
    {
        var colon = line.IndexOf(':');
        if (colon is > 0 and <= 80)
        {
            if (Known(line[..colon], outlets) is { } outlet) return new OutletDifference(outlet, line[(colon + 1)..].Trim());
        }
        return new OutletDifference(string.Empty, line);
    }

    private static IEnumerable<string> Bullets(string section) =>
        section.Split('\n')
            .Select(line => Flat(Bullet().Replace(line.Trim(), string.Empty)))
            .Where(line => line.Length >= 12)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string Flat(string text) => Whitespace().Replace(text, " ").Trim();

    private static string Clip(string text, int max)
    {
        var flat = Flat(text);
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Think();

    [GeneratedRegex(@"^\s*#*\s*(CLAIM|SUMMARY|AGREED|DIFFERENCES)\s*:", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Label();

    [GeneratedRegex(@"^([-*•–]|\d+[.)])\s*")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // "<fact> [A, B, C]", the brackets last (a full stop after them is allowed).
    [GeneratedRegex(@"^(.*?)\s*\[([^\]]*)\]\s*[.;]?\s*$")]
    private static partial Regex Bracketed();
}
