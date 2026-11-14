using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// World News search (13-11-26, Grant's priority): type a term or a question and see every story
// with similar reports: by meaning (the stories' embeddings, so "hospital ransomware" finds "clinic
// systems encrypted") and by words (the full-text archive, which reaches further back), merged and
// ranked. Cyber stories carry their status (CyberStatus) and the CVEs their reports name.
public sealed record FoundStory(
    EventOverview Event,
    double? Similarity,
    int MatchingReports,
    string? Kind,
    string? CyberStatus,
    IReadOnlyList<string> Cves)
{
    public bool IsCyber => CyberStatus is not null;

    // A word match, or a close match by meaning; the rest are shown as loosely related.
    public bool IsStrong => MatchingReports > 0 || Similarity >= WorldNewsFinder.StrongSimilarity;

    public string MatchedBy => (Similarity, MatchingReports) switch
    {
        ({ }, > 0) => "meaning and words",
        ({ }, _) => "meaning",
        _ => "words",
    };
}

public sealed record FindResult(string Query, IReadOnlyList<FoundStory> Stories, string? Note);

public static partial class WorldNewsFinder
{
    public const double MinSimilarity = 0.45;
    // Measured 13-11-26 on the local copy: "hospital cyber attack" -> the hospital data-theft story
    // at 0.66; unrelated stories (a cat virus, a game update) scored 0.46-0.52 for one-word queries.
    public const double StrongSimilarity = 0.55;
    public const double SimilarToStory = 0.55;
    public const int Limit = 40;
    // Story vectors are kept 8 days (WorldNewsStore.ReleaseStaleVectors), so meaning reaches back a
    // week; words reach as far as the archive.
    public static readonly TimeSpan MeaningWindow = TimeSpan.FromDays(7);
    public static readonly IReadOnlyList<int> RangeDays = [1, 3, 7, 30];

    public static IReadOnlyList<string> Cves(IEnumerable<string?> texts) =>
        texts.Where(t => !string.IsNullOrEmpty(t)).SelectMany(t => Cve().Matches(t!).Select(m => m.Value.ToUpperInvariant()))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    // The words as the archive is searched: each plain word of four letters or more also matches
    // its longer forms (hack -> hacked, hackers), and a CVE id is kept whole as a phrase (FTS splits
    // it at the hyphens). Typed operators, quotes or * are left exactly as written.
    public static string WordQuery(string query)
    {
        var text = query.Trim();
        if (text.Contains('"') || text.Contains('*') || Operator().IsMatch(text)) return text;
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word =>
            Cve().IsMatch(word) ? $"\"{word.ToUpperInvariant()}\""
            : word.Count(char.IsLetterOrDigit) >= 4 && word.All(char.IsLetterOrDigit) ? word + "*" : word));
    }

    public static bool IsCyberQuery(string query) => CyberWords().IsMatch(query) || Cve().IsMatch(query);

    // Meaning hits (event, similarity) and word hits (event, matching reports), one list: similarity
    // first, a few matching reports lifting a story, word-only stories after by how many reports match.
    public static IReadOnlyList<(string EventId, double? Similarity, int Reports)> Merge(
        IEnumerable<(string EventId, double Similarity)> meaning, IEnumerable<(string EventId, int Reports)> words, int limit = Limit)
    {
        var merged = new Dictionary<string, (double? Similarity, int Reports)>(StringComparer.Ordinal);
        foreach (var (id, similarity) in meaning) merged[id] = (similarity, 0);
        foreach (var (id, reports) in words)
            merged[id] = merged.TryGetValue(id, out var hit) ? (hit.Similarity, reports) : (null, reports);
        return merged
            .Select(pair => (EventId: pair.Key, pair.Value.Similarity, pair.Value.Reports))
            .OrderByDescending(hit => Score(hit.Similarity, hit.Reports))
            .Take(limit)
            .ToList();
    }

    public static double Score(double? similarity, int reports) =>
        (similarity ?? 0.35) + Math.Min(0.25, Math.Log2(1 + reports) * 0.05);

    // The cyber status a story's headlines give, or null for a story that isn't about an attack.
    // A story counts as cyber when it was classified so, or when a headline plainly says so (the
    // classifier hadn't placed "Hackers release AfD party emails", measured 13-11-26).
    public static string? CyberStatusOf(string? kind, IEnumerable<string?> headlines)
    {
        var list = headlines.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        return kind == SituationKinds.Cyber || list.Any(IsCyberHeadline) ? CyberStatus.Of(list) : null;
    }

    // Stricter than a query: a headline about an attack, a breach or an exploited flaw.
    public static bool IsCyberHeadline(string? headline) => headline is not null && (CyberHeadline().IsMatch(headline) || Cve().IsMatch(headline));

    [GeneratedRegex(@"\b(cyber[- ]?(attack|attacks|attacked|crime|criminals?|espionage|incident|breach)|ransomware|malware|spyware|data breach|breach(ed)? (of )?(customer|patient|user|personal) data|hack(ed|ers?|ing)\b|ddos|phishing|zero[- ]day|data leak|leak site|botnet|wiper|infostealer|exploited (flaw|vulnerability|bug))", RegexOptions.IgnoreCase)]
    private static partial Regex CyberHeadline();

    [GeneratedRegex(@"\b(AND|OR|NOT)\b")]
    private static partial Regex Operator();

    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,7}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Cve();

    [GeneratedRegex(@"\b(cyber\w*|ransomware|malware|breach(es|ed)?|hack(s|ed|ers?|ing)?|ddos|phishing|zero[- ]day|exploit(s|ed)?|vulnerabilit(y|ies)|data leak|leak site|botnet|spyware|wiper|apt\d*|threat actor|outage|compromis(e|ed))\b", RegexOptions.IgnoreCase)]
    private static partial Regex CyberWords();
}
