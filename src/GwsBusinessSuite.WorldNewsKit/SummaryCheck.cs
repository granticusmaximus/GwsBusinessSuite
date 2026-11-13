using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// B7 (13-11-26): the AI summary checked against its sources. A sentence is flagged when a number or a
// proper name in it isn't in any report's headline, English headline or description. Sentences with
// nothing checkable aren't flagged. The full articles aren't checked, so a flag means "not found in
// what World News holds", not "false".
public sealed record SummarySentence(string Text, IReadOnlyList<string> NotFound)
{
    public bool Supported => NotFound.Count == 0;
}

public static partial class SummaryCheck
{
    private static readonly HashSet<string> Ordinary = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "This", "That", "These", "Those", "There", "Their", "They", "While", "After", "Before", "During", "According",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "January", "February", "March", "April",
        "May", "June", "July", "August", "September", "October", "November", "December", "However", "Meanwhile", "Several",
        "Some", "Many", "Other", "Others", "Officials", "Reports", "Outlets", "Sources", "Both", "Also",
    };

    public static IReadOnlyList<SummarySentence> Check(string summary, IEnumerable<string?> sources)
    {
        var haystack = Normalize(string.Join("\n", sources.Where(text => !string.IsNullOrWhiteSpace(text))));
        return Sentences().Split(summary.Trim())
            .Where(sentence => sentence.Trim().Length > 0)
            .Select(sentence =>
            {
                var missing = Checkable(sentence)
                    .Where(token => !haystack.Contains(Normalize(token), StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new SummarySentence(sentence.Trim(), missing);
            })
            .ToList();
    }

    // Numbers (as written, without separators) and capitalised names not at the sentence's start.
    public static IEnumerable<string> Checkable(string sentence)
    {
        foreach (Match number in Number().Matches(sentence)) yield return number.Value.Replace(",", "").TrimEnd('.');
        var words = Word().Matches(sentence).Select(match => match.Value).ToList();
        for (var i = 1; i < words.Count; i++)
        {
            var word = words[i];
            if (word.Length >= 3 && char.IsUpper(word[0]) && !Ordinary.Contains(word)) yield return word;
        }
    }

    private static string Normalize(string text) => text.Replace(",", "").Replace("’", "'");

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9""“])")]
    private static partial Regex Sentences();

    [GeneratedRegex(@"\b\d[\d,]*(\.\d+)?\b")]
    private static partial Regex Number();

    [GeneratedRegex(@"[\p{L}][\p{L}'’-]*")]
    private static partial Regex Word();
}
