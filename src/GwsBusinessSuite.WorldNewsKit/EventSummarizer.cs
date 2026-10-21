using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// Asks a chat model for an answer: (system prompt, user prompt) -> text. The host decides which
// model and server, so the kit doesn't depend on the server's Ollama service.
public delegate Task<string> TextGenerator(string system, string prompt, CancellationToken ct);

public sealed record SummaryOptions
{
    // Only events at least this widely covered are summarized.
    public int MinOutlets { get; init; } = 3;
    public int PerRun { get; init; } = 3;
    public TimeSpan Window { get; init; } = TimeSpan.FromHours(24);
    // A small model on a shared CPU takes tens of seconds; 2 minutes is the app's usual ceiling.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    // About 2,000 tokens of Latin script; scripts like Japanese can use up to a token a character,
    // so this still fits an 8k context.
    public int MaxPromptChars { get; init; } = 6000;
}

public sealed record SummaryRunReport(int Written, IReadOnlyList<string> Notes);

// Writes an English headline and summary for the busiest events, from their reports' headlines
// and outlets' descriptions (never fetched article text). An event is summarized again only once
// it has grown (see WorldNewsStore.EventsDueSummary), and every version is kept.
public sealed partial class EventSummarizer(WorldNewsStore store, TextGenerator generate, string model, TimeProvider time,
    Func<DateTimeOffset, string> formatTime, SummaryOptions options)
{
    public const string SystemPrompt = """
        You summarize news coverage of one event from the reports listed. The reports come from outlets in many countries and languages.
        Rules:
        - State only facts the headlines and descriptions give. If a detail (a number, a date, a name, who did what) isn't there, leave it out.
        - Don't guess anyone's role, team or side. Fewer facts are better than a wrong one.
        - Write in English, in your own words, without copying sentences.
        Answer in exactly this form:
        HEADLINE: one plain English headline, at most 12 words
        SUMMARY: 2 to 4 short sentences on what happened, where, and who is involved. If outlets disagree on a number or a fact, say so and name them.
        """;

    public async Task<SummaryRunReport> RunAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        var written = 0;
        if (options.PerRun <= 0) return new SummaryRunReport(0, notes);

        foreach (var e in store.EventsDueSummary(time.GetUtcNow() - options.Window, options.MinOutlets, options.PerRun))
        {
            var prompt = EventDocument.ToPrompt(e, store.ArticlesForEvent(e.Id), formatTime, options.MaxPromptChars);
            string raw;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(options.Timeout);
                try
                {
                    raw = await generate(SystemPrompt, prompt, timeout.Token);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // The model is down or too slow: the rest would fail the same way, so wait for the next read.
                    notes.Add(ex is OperationCanceledException
                        ? $"{model} didn't write a summary within {options.Timeout.TotalSeconds:0} seconds"
                        : $"{model} couldn't write a summary: {ex.Message}");
                    break;
                }
            }

            if (Parse(raw) is not { } parsed)
            {
                notes.Add($"{model} gave no usable summary for \"{e.Title}\"");
                continue;
            }
            store.SaveSummary(e.Id, parsed.Headline, parsed.Text, model, e.ArticleCount, time.GetUtcNow());
            written++;
        }
        return new SummaryRunReport(written, notes);
    }

    // "HEADLINE: …" and "SUMMARY: …", tolerating markdown bold, a reasoning model's <think> block
    // and a missing headline. Null when there's no real summary.
    public static (string? Headline, string Text)? Parse(string raw)
    {
        var text = Think().Replace(raw ?? string.Empty, string.Empty).Replace("**", string.Empty).Trim();
        var headline = Field(text, Headline());
        var summaryAt = SummaryLabel().Match(text);
        var summary = summaryAt.Success ? text[(summaryAt.Index + summaryAt.Length)..] : text;
        summary = Whitespace().Replace(HeadlineLine().Replace(summary, string.Empty), " ").Trim();
        if (summary.Length < 40) return null;
        return (headline is { Length: > 0 and <= 160 } ? headline.Trim('"', '“', '”', ' ') : null, summary);
    }

    private static string? Field(string text, Regex pattern) =>
        pattern.Match(text) is { Success: true } match ? match.Groups[1].Value.Trim() : null;

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Think();

    [GeneratedRegex(@"^\s*#*\s*HEADLINE\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Headline();

    [GeneratedRegex(@"^\s*#*\s*HEADLINE\s*:.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex HeadlineLine();

    [GeneratedRegex(@"^\s*#*\s*SUMMARY\s*:", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SummaryLabel();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
