using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Assistant;

// What a page tells the SentinelGPT side panel about itself: the record that's open, its
// details as plain text, and up to three suggested questions. Pages that set nothing still get
// their title, heading, visible text and any selected text (AssistantCapture).
public sealed record AssistantPageContext(
    string PageName,
    string? RecordKind = null,
    string? RecordTitle = null,
    string? Details = null,
    IReadOnlyList<string>? Suggestions = null,
    // The open record's id, so SentinelGPT's agent can act on "this" (30-10-26).
    string? RecordId = null);

// Read from the browser each time a question is sent.
public sealed record AssistantCapture(string? Title, string? Heading, string? Selection, string? VisibleText);

public sealed record AssistantTurn(string Question, string Answer);

public static partial class AssistantPrompt
{
    public const int MaxSelection = 4_000;
    public const int MaxVisibleText = 6_000;
    public const int MaxDetails = 6_000;
    public const int MaxHistoryTurns = 6;
    public const int MaxHistoryAnswer = 1_500;

    public const string SystemPrompt =
        "You are SentinelGPT, the assistant built into the GWS Business Suite admin portal. " +
        "Answer from the current page context below and what the user tells you. If the context doesn't hold the answer, say so rather than guessing. " +
        "You can't change anything in the app yourself: when asked to write something (a reply, an email, a note), write it as a draft for the user to review, with no preamble. " +
        "Be concise. Write dates as dd-MM-yy and times as 24-hour HH:mm.";

    public static IReadOnlyList<string> Suggestions(AssistantPageContext? page, bool hasSelection)
    {
        var suggestions = new List<string>();
        if (hasSelection) suggestions.Add("Explain the selected text");
        suggestions.AddRange(page?.Suggestions is { Count: > 0 } own ? own : ["Summarize this page", "What needs my attention here?"]);
        return suggestions.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    // Where the user is, as plain text for the model. A page's own record details describe it
    // more reliably than its rendered text, so the visible text is only used without them.
    public static string DescribeContext(AssistantPageContext? page, AssistantCapture? capture)
    {
        var text = new StringBuilder();
        text.AppendLine($"Page: {page?.PageName ?? Clean(capture?.Heading) ?? Clean(capture?.Title) ?? "an admin page"}");
        if (page?.RecordKind is { } kind && page.RecordTitle is { } title)
            text.AppendLine($"Open record: {kind} \"{title}\"" + (page.RecordId is { } id ? $" (id {id})" : string.Empty));
        if (Clip(page?.Details, MaxDetails) is { } details)
        {
            text.AppendLine("Record details:").AppendLine(details);
        }
        else if (Clip(capture?.VisibleText, MaxVisibleText) is { } visible)
        {
            text.AppendLine("What the page shows (may be cut short):").AppendLine(visible);
        }
        if (Clip(capture?.Selection, MaxSelection) is { } selection) text.AppendLine("Text the user selected:").AppendLine(selection);
        return text.ToString().TrimEnd();
    }

    // The local model keeps no conversation, so the recent turns travel with each question.
    public static (string System, string Prompt) ForLocalModel(string context, IReadOnlyList<AssistantTurn> history, string question)
    {
        var prompt = new StringBuilder();
        foreach (var turn in history.TakeLast(MaxHistoryTurns))
        {
            prompt.AppendLine($"User: {turn.Question}").AppendLine($"Assistant: {Clip(turn.Answer, MaxHistoryAnswer)}").AppendLine();
        }
        prompt.Append($"User: {question.Trim()}");
        return ($"{SystemPrompt}\n\n## Current page\n{context}", prompt.ToString());
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = ExtraBlankLines().Replace(string.Join('\n', value.Replace("\r", string.Empty).Split('\n').Select(line => line.Trim())), "\n\n").Trim();
        return compact.Length <= max ? compact : compact[..max].TrimEnd() + " …";
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

}
