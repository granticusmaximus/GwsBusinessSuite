using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Boards;

// SentinelGPT on a board (Boards K8b): a standup, what's stuck and why, and breaking a card into
// sub-items. The board gathers the facts (KanbanBrief) and they're shown as they are; the local
// model (the Ollama on the person's own machine) only writes them up, told to use nothing else.
// Without a reachable model the facts stand on their own.
public sealed record KanbanBriefMove(Guid RowId, string? Key, string Title, string? From, string To, string By, DateTimeOffset At);
public sealed record KanbanBriefCard(Guid RowId, string? Key, string Title, string Column, double DaysInColumn, bool Blocked, string? BlockedReason,
    IReadOnlyList<string> Assignees, bool PastTypical);

// Since: the start of the last working day. Typical: the board's 85% cycle time in days, if known.
public sealed record KanbanBrief(
    DateTimeOffset Since,
    IReadOnlyList<KanbanBriefMove> Moves,
    IReadOnlyList<KanbanBriefCard> Stuck,
    IReadOnlyList<string> OverLimit,
    double? Typical);

public static partial class KanbanAssistant
{
    // The start of the last working day before now: Friday's for a Monday, yesterday's otherwise.
    public static DateTimeOffset StandupSince(DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.LocalDateTime).AddDays(-1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(-1);
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static string Name(string? key, string title) => key is null ? $"\"{title}\"" : $"{key} \"{title}\"";
    private static string Days(double days) => days < 1 ? "under a day" : $"{Math.Floor(days).ToString(CultureInfo.InvariantCulture)} {(Math.Floor(days) == 1 ? "day" : "days")}";

    // The facts, one per line, as the prompt gives them and as the board shows them without a model.
    public static IReadOnlyList<string> StandupFacts(KanbanBrief brief, IReadOnlyCollection<string> completeColumns)
    {
        var lines = new List<string>();
        var latest = brief.Moves.GroupBy(move => move.RowId).Select(group => group.OrderBy(move => move.At).ToList()).ToList();
        foreach (var moves in latest.Where(moves => completeColumns.Contains(moves[^1].To)))
            lines.Add($"Done: {Name(moves[^1].Key, moves[^1].Title)} reached {moves[^1].To} ({moves[^1].By}).");
        foreach (var moves in latest.Where(moves => !completeColumns.Contains(moves[^1].To)))
            lines.Add($"Moved: {Name(moves[^1].Key, moves[^1].Title)} {(moves[0].From is { } from ? $"from {from} " : "")}to {moves[^1].To} ({moves[^1].By}).");
        foreach (var card in brief.Stuck.Where(card => card.Blocked))
            lines.Add($"Blocked: {Name(card.Key, card.Title)} in {card.Column}{(string.IsNullOrWhiteSpace(card.BlockedReason) ? "" : $" - {card.BlockedReason}")}.");
        if (lines.Count == 0) lines.Add("Nothing moved since the last working day.");
        return lines;
    }

    public static IReadOnlyList<string> StuckFacts(KanbanBrief brief)
    {
        var lines = new List<string>();
        foreach (var card in brief.Stuck)
        {
            var why = new List<string>();
            if (card.Blocked) why.Add(string.IsNullOrWhiteSpace(card.BlockedReason) ? "marked blocked" : $"blocked: {card.BlockedReason}");
            if (card.PastTypical && brief.Typical is { } typical) why.Add($"{Days(card.DaysInColumn)} in {card.Column}, past the {Days(typical)} 85% of cards finish within");
            else why.Add($"{Days(card.DaysInColumn)} in {card.Column}");
            if (card.Assignees.Count == 0) why.Add("nobody assigned");
            else why.Add($"with {string.Join(", ", card.Assignees)}");
            lines.Add($"{Name(card.Key, card.Title)}: {string.Join("; ", why)}.");
        }
        foreach (var column in brief.OverLimit) lines.Add($"{column} is over its WIP limit.");
        if (lines.Count == 0) lines.Add("Nothing looks stuck: no blocked cards, nothing past its usual time, every column within its limit.");
        return lines;
    }

    public const string StandupSystem =
        "You write a short daily standup for a small team from facts about their board. Use only the facts given; " +
        "never invent cards, people or progress. Reply in plain text with three headings on their own lines - Done, " +
        "In progress, Blockers - each followed by short '- ' bullet points (or '- Nothing' if none). No preamble, no sign-off.";

    public const string StuckSystem =
        "You review a work board for what's stuck. Use only the facts given; never invent cards or causes. For each stuck " +
        "card write one '- ' bullet: the card, why it's stuck in a few words, and one concrete next step. End with one " +
        "short line on the board as a whole. Plain text, no preamble.";

    public const string BreakdownSystem =
        "You break one work item into the small concrete steps that finish it. Reply with only JSON: " +
        "{\"steps\":[\"...\",\"...\"]} - 3 to 7 steps, each a short imperative under 80 characters, in the order to do them. " +
        "Use the card's own details; don't add steps it doesn't need.";

    public static string FactsPrompt(string board, IReadOnlyList<string> facts) =>
        $"Board: {board}\nFacts:\n{string.Join("\n", facts.Select(fact => "- " + fact))}";

    public static string BreakdownPrompt(string title, string? page) =>
        string.IsNullOrWhiteSpace(page) ? $"Card: {title}" : $"Card: {title}\nWhat its page says:\n{(page.Length > 2000 ? page[..2000] : page)}";

    // Steps from the model's answer: {"steps": [...]}, a bare array, or failing that its bullet
    // lines. Trimmed, de-duplicated, at most 10, each at most 120 characters.
    public static IReadOnlyList<string> ParseSteps(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var steps = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(output.Trim());
            var array = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement
                : document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.EnumerateObject()
                    .FirstOrDefault(property => property.Value.ValueKind == JsonValueKind.Array) is { Value.ValueKind: JsonValueKind.Array } property ? property.Value
                : default;
            if (array.ValueKind == JsonValueKind.Array)
                steps.AddRange(array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString()));
        }
        catch (JsonException)
        {
            steps.AddRange(output.Split('\n').Select(line => Bullet().Replace(line, "")).Where(line => line.Length > 0));
        }
        return steps.Select(step => step.Trim().Trim('"').Trim())
            .Where(step => step.Length > 1)
            .Select(step => step.Length > 120 ? step[..117].TrimEnd() + "…" : step)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s*")]
    private static partial Regex Bullet();
}
