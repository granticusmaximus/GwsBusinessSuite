using System.Globalization;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Boards;

// One-line cards (Boards K8): "Fix login !high #bug @grant fri ~3" is a card called "Fix login",
// High priority, labelled Bug, assigned to grant, due Friday, 3 points. Only what fits the board
// counts - a #word that isn't one of its labels stays in the title - and a day word only at the end
// ("Plan the monday meeting" keeps its Monday), so nothing is guessed. The board shows each token
// as a chip while you type.
public static class KanbanQuickTokens
{
    public const string Priority = "priority";
    public const string Label = "label";
    public const string Person = "person";
    public const string Due = "due";
    public const string Estimate = "estimate";
}

public sealed record KanbanQuickToken(string Kind, string Text, string Label, string? Color = null);

public sealed record KanbanQuickCard(
    string Title,
    string? PriorityOptionId,
    IReadOnlyList<string> LabelOptionIds,
    IReadOnlyList<string> People,
    DateOnly? Due,
    decimal? Estimate,
    IReadOnlyList<KanbanQuickToken> Tokens);

public static partial class KanbanQuickParse
{
    private static readonly string[][] DayNames =
    [
        ["sun", "sunday"], ["mon", "monday"], ["tue", "tues", "tuesday"], ["wed", "weds", "wednesday"],
        ["thu", "thur", "thurs", "thursday"], ["fri", "friday"], ["sat", "saturday"]
    ];

    public static KanbanQuickCard Parse(string? text, KanbanBoard board, DateOnly today, string me)
    {
        var words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var title = new List<string>();
        var tokens = new List<KanbanQuickToken>();
        string? priority = null;
        var labels = new List<string>();
        var people = new List<string>();
        decimal? estimate = null;

        foreach (var word in words)
        {
            if (word.Length > 1 && word[0] == '!' && board.Fields.PriorityPropertyId is not null && Option(board.PriorityOptions, word[1..], numbered: true) is { } p)
            {
                priority = p.Id;
                tokens.RemoveAll(token => token.Kind == KanbanQuickTokens.Priority);
                tokens.Add(new KanbanQuickToken(KanbanQuickTokens.Priority, word, p.Label, p.Color));
            }
            else if (word.Length > 1 && word[0] == '#' && board.Fields.LabelsPropertyId is not null && Option(board.LabelOptions, word[1..], numbered: false) is { } l)
            {
                if (!labels.Contains(l.Id))
                {
                    labels.Add(l.Id);
                    tokens.Add(new KanbanQuickToken(KanbanQuickTokens.Label, word, l.Label, l.Color));
                }
            }
            else if (word.Length > 1 && word[0] == '@' && board.Fields.AssigneePropertyId is not null && Username().IsMatch(word[1..]))
            {
                var name = string.Equals(word[1..], "me", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(me) ? me : word[1..];
                if (!people.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    people.Add(name);
                    tokens.Add(new KanbanQuickToken(KanbanQuickTokens.Person, word, name));
                }
            }
            else if (word.Length > 1 && word[0] == '~' && board.Fields.EstimatePropertyId is not null
                && decimal.TryParse(word[1..], NumberStyles.Number, CultureInfo.InvariantCulture, out var points) && points is >= 0 and <= 999)
            {
                estimate = points;
                tokens.RemoveAll(token => token.Kind == KanbanQuickTokens.Estimate);
                tokens.Add(new KanbanQuickToken(KanbanQuickTokens.Estimate, word, $"{points.ToString("0.#", CultureInfo.InvariantCulture)} {(points == 1 ? "point" : "points")}"));
            }
            else
            {
                title.Add(word);
            }
        }

        // A due day only as the last word of the title.
        DateOnly? due = null;
        if (board.Fields.DuePropertyId is not null && title.Count > 1 && DayOf(title[^1], today) is { } day)
        {
            due = day;
            var word = title[^1];
            title.RemoveAt(title.Count - 1);
            tokens.Add(new KanbanQuickToken(KanbanQuickTokens.Due, word,
                $"{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek)} {day.ToString("dd-MM-yy", CultureInfo.InvariantCulture)}"));
        }

        return new KanbanQuickCard(string.Join(' ', title), priority, labels, people, due, estimate, tokens);
    }

    // An option by its label, or the start of it ("!hi" is High); for priorities !1 is the first.
    private static KanbanOption? Option(IReadOnlyList<KanbanOption> options, string text, bool numbered)
    {
        if (numbered && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return number >= 1 && number <= options.Count ? options[number - 1] : null;
        return options.FirstOrDefault(option => string.Equals(option.Label, text, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(option => option.Label.StartsWith(text, StringComparison.OrdinalIgnoreCase));
    }

    // today, tomorrow, a day of the week (the next one, today included), or a date as the app
    // writes them (dd-MM or dd-MM-yy).
    private static DateOnly? DayOf(string word, DateOnly today)
    {
        var lower = word.ToLowerInvariant();
        if (lower is "today" or "tod") return today;
        if (lower is "tomorrow" or "tmrw" or "tmr") return today.AddDays(1);
        for (var index = 0; index < DayNames.Length; index++)
        {
            if (!DayNames[index].Contains(lower)) continue;
            var ahead = (index - (int)today.DayOfWeek + 7) % 7;
            return today.AddDays(ahead);
        }
        var match = ShortDate().Match(lower);
        if (!match.Success) return null;
        var dayOfMonth = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var year = match.Groups["y"].Success ? 2000 + int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture) : today.Year;
        if (month is < 1 or > 12 || dayOfMonth < 1 || dayOfMonth > DateTime.DaysInMonth(year, month)) return null;
        var date = new DateOnly(year, month, dayOfMonth);
        // Without a year, a date already gone this year means next year's.
        return !match.Groups["y"].Success && date < today ? date.AddYears(1) : date;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Username();

    [GeneratedRegex(@"^(?<d>\d{1,2})[-/](?<m>\d{1,2})(?:[-/](?<y>\d{2}))?$")]
    private static partial Regex ShortDate();
}
