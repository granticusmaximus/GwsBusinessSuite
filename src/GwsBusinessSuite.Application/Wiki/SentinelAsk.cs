using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// "Ask the workspace" (Sentinel item 2). The server finds the pages that answer a question among
// those the asker can view, and hands the model a few numbered excerpts; the model answers only
// from them, citing [n]. It runs on the asker's own computer when it has Ollama (the browser
// relay) and on the server's small model otherwise. Every question is logged (SentinelQuestion),
// so unanswered ones can feed knowledge gaps (item 23).

public sealed record SentinelAskSource(int Number, Guid TargetId, bool IsDatabase, Guid? RowId, string Title, string Excerpt, bool Verified, int Score);

public sealed record SentinelAskContext(string Question, IReadOnlyList<SentinelAskSource> Sources)
{
    public bool HasSources => Sources.Count > 0;
}

public sealed record SentinelAskAnswer(string Text, IReadOnlyList<int> Cited, bool NotFound, bool Uncited);

public interface ISentinelAskService
{
    // Retrieval only: the numbered sources (pages and databases the user can view) for a question.
    Task<SentinelAskContext> PrepareAsync(string question, string username, CancellationToken cancellationToken = default);

    // The fallback when the asker's computer has no Ollama: the server's own (small) model.
    Task<SentinelAskAnswer> AnswerOnServerAsync(SentinelAskContext context, CancellationToken cancellationToken = default);

    // The model the server answers with (the SentinelGPT model or the site's override).
    Task<string> ServerModelAsync(CancellationToken cancellationToken = default);

    // Logged after every answer, wherever it was written.
    Task RecordAsync(SentinelAskContext context, SentinelAskAnswer answer, string username, string answeredBy, string model, CancellationToken cancellationToken = default);
}

public static partial class SentinelAsk
{
    public const int MaxQuestionLength = 500;
    public const int MaxSources = 5;
    public const int MaxExcerptCharacters = 1200;
    public const string NotFoundToken = "NOT FOUND";

    public const string SystemPrompt =
        "You answer questions about a team's workspace using ONLY the numbered sources you are given. " +
        "Rules: (1) End every sentence that states a fact with the number of the source it came from, like [1] or [2, 3]. " +
        "(2) Never use outside knowledge and never mention a source number that isn't listed. " +
        "(3) If the sources don't contain the answer, reply with exactly: " + NotFoundToken + " " +
        "(4) Be brief and direct: at most 150 words, plain sentences, no headings or markdown.";

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "an", "and", "are", "as", "at", "be", "can", "could", "did", "do", "does", "for", "from", "has",
        "have", "how", "i", "if", "in", "into", "is", "it", "its", "me", "my", "of", "on", "or", "our", "please",
        "should", "so", "tell", "that", "the", "their", "there", "these", "this", "to", "us", "was", "we", "were",
        "what", "whats", "when", "where", "which", "who", "why", "will", "with", "would", "you", "your", "any", "all"
    };

    public static string Normalize(string? question)
    {
        var text = Whitespace().Replace(question ?? string.Empty, " ").Trim();
        return text.Length <= MaxQuestionLength ? text : text[..MaxQuestionLength];
    }

    // The words that matter, for keyword search: no filler, no punctuation, longest first.
    public static IReadOnlyList<string> KeyTerms(string question) =>
        WordPattern().Matches(question.ToLowerInvariant())
            .Select(m => m.Value.Trim('\''))
            .Where(word => word.Length >= 3 && !StopWords.Contains(word.Replace("'", string.Empty)))
            .Distinct()
            .OrderByDescending(word => word.Length)
            .ToList();

    // The passages of a page that best match the question, kept in page order, within the budget.
    // The first non-empty passage (usually what the page is about) always comes along.
    public static string Excerpt(IReadOnlyList<WikiBlock> blocks, IReadOnlyList<string> terms, int budget = MaxExcerptCharacters)
    {
        var passages = blocks
            .Select(block => Whitespace().Replace(block.PlainText, " ").Trim())
            .Where(text => text.Length > 0)
            .Select((text, index) => (Text: text, Index: index, Hits: terms.Count(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (passages.Count == 0) return string.Empty;

        var chosen = new SortedDictionary<int, string> { [passages[0].Index] = passages[0].Text };
        var used = passages[0].Text.Length;
        foreach (var passage in passages.Skip(1).Where(p => p.Hits > 0).OrderByDescending(p => p.Hits).ThenBy(p => p.Index))
        {
            if (used + passage.Text.Length > budget) continue;
            chosen[passage.Index] = passage.Text;
            used += passage.Text.Length;
        }
        // Little matched: fill with what follows the opening, so short pages come through whole.
        foreach (var passage in passages.Skip(1).Where(p => !chosen.ContainsKey(p.Index)))
        {
            if (used + passage.Text.Length > budget) break;
            chosen[passage.Index] = passage.Text;
            used += passage.Text.Length;
        }
        var text = string.Join("\n", chosen.Values);
        return text.Length <= budget ? text : text[..budget].TrimEnd() + "…";
    }

    public static string UserPrompt(SentinelAskContext context)
    {
        var prompt = new StringBuilder("Question: ").Append(context.Question).Append("\n\nSources:\n");
        foreach (var source in context.Sources)
        {
            prompt.Append('[').Append(source.Number).Append("] ").Append(source.Title);
            if (source.Verified) prompt.Append(" (verified)");
            prompt.Append('\n').Append(source.Excerpt).Append("\n\n");
        }
        return prompt.ToString().TrimEnd();
    }

    // Checks the model's answer against the sources: citations to numbers that weren't given are
    // removed, "NOT FOUND" (or no sources at all) becomes the not-found answer, and an answer with
    // no citation left is flagged so the page can say so.
    public static SentinelAskAnswer Clean(string? modelOutput, SentinelAskContext context)
    {
        var raw = (modelOutput ?? string.Empty).Trim();
        if (!context.HasSources || raw.Length == 0 || raw.TrimEnd('.', '!').Equals(NotFoundToken, StringComparison.OrdinalIgnoreCase)
            || (raw.StartsWith(NotFoundToken, StringComparison.OrdinalIgnoreCase) && raw.Length < 60))
            return NotFound();

        var valid = context.Sources.Select(s => s.Number).ToHashSet();
        var cited = new List<int>();
        var text = Citation().Replace(raw, match =>
        {
            var numbers = match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(n => int.TryParse(n, out var value) ? value : -1).Where(valid.Contains).Distinct().ToList();
            cited.AddRange(numbers);
            return numbers.Count == 0 ? string.Empty : $" [{string.Join(", ", numbers)}]";
        });
        text = Whitespace().Replace(text.Replace(NotFoundToken, string.Empty, StringComparison.OrdinalIgnoreCase), " ")
            .Replace(" .", ".").Replace(" ,", ",").Trim();
        if (text.Length == 0) return NotFound();
        var distinct = cited.Distinct().Order().ToList();
        return new SentinelAskAnswer(text, distinct, false, distinct.Count == 0);
    }

    // "Refunds take 14 days [1, 2]." -> ("Refunds take 14 days", null), ("", [1, 2]), (".", null):
    // the page shows the text pieces and a button per cited number.
    public static IReadOnlyList<(string Text, IReadOnlyList<int>? Numbers)> SplitCitations(string text)
    {
        var parts = new List<(string, IReadOnlyList<int>?)>();
        var last = 0;
        foreach (Match match in CitationOnly().Matches(text ?? string.Empty))
        {
            if (match.Index > last) parts.Add((text![last..match.Index], null));
            parts.Add((string.Empty, match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).Select(int.Parse).ToList()));
            last = match.Index + match.Length;
        }
        if (last < (text ?? string.Empty).Length) parts.Add((text![last..], null));
        return parts;
    }

    public static SentinelAskAnswer NotFound() =>
        new("I couldn't find this in the pages you can see in the workspace.", [], true, false);

    [GeneratedRegex(@"\s*\[(\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex Citation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\[(\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex CitationOnly();

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex WordPattern();
}
