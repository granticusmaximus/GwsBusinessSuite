using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// English headlines for a story's reports in other languages (06-11-26), so a reader comparing 22
// languages' coverage can read every source. Written by the same model as the analysis, in batches,
// and kept per article; a headline is never translated twice.
public static partial class HeadlineTranslator
{
    public const int MaxPerStory = 80;
    public const int MaxPerCall = 40;

    public const string SystemPrompt = """
        You translate news headlines into plain English.
        Each input line is a number, a full stop, and a headline in another language.
        Answer with exactly one line per headline, in the same order and with the same number: the number, a full stop, and the English translation.
        Write nothing else: no notes, no original text, no quotation marks.
        """;

    public static bool IsEnglish(string language) => language.Equals("English", StringComparison.OrdinalIgnoreCase);

    // The headlines still to translate: one per outlet first (newest), then the rest newest first,
    // up to max, skipping wire copies (EventDocument.Sources) and those already translated.
    public static IReadOnlyList<WorldArticle> Choose(IReadOnlyList<WorldArticle> articles, IReadOnlyDictionary<string, string> known, int max = MaxPerStory)
    {
        var pending = EventDocument.Sources(articles).Select(source => source.Report)
            .Where(a => !IsEnglish(a.Language) && !known.ContainsKey(a.Id) && !string.IsNullOrWhiteSpace(a.Title))
            .OrderByDescending(a => a.SeenAt)
            .ToList();
        var firstPerOutlet = pending.GroupBy(a => a.Outlet).Select(outlet => outlet.First()).ToList();
        return firstPerOutlet.Concat(pending.Except(firstPerOutlet)).Take(max).ToList();
    }

    public static string Prompt(IReadOnlyList<WorldArticle> batch)
    {
        var text = new StringBuilder();
        for (var i = 0; i < batch.Count; i++) text.AppendLine($"{i + 1}. {Flat(batch[i].Title)}");
        return text.ToString().TrimEnd();
    }

    // Article id -> English, for the lines that came back numbered and changed.
    public static IReadOnlyDictionary<string, string> Parse(string raw, IReadOnlyList<WorldArticle> batch)
    {
        var english = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = Think().Replace(raw ?? string.Empty, string.Empty);
        foreach (Match match in Numbered().Matches(text))
        {
            if (!int.TryParse(match.Groups[1].Value, out var number) || number < 1 || number > batch.Count) continue;
            var line = Flat(match.Groups[2].Value).Trim('"', '“', '”', '«', '»', ' ');
            var article = batch[number - 1];
            if (line.Length < 3 || line.Equals(Flat(article.Title), StringComparison.Ordinal)) continue;
            english.TryAdd(article.Id, line.Length <= 300 ? line : line[..300].TrimEnd() + "…");
        }
        return english;
    }

    private static string Flat(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Think();

    [GeneratedRegex(@"^\s*(\d+)\s*[.)]\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex Numbered();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
