using System.Globalization;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Boards;

// Repeating cards (Boards K6): a card template made again on a schedule, through Sentinel's row
// recurrences. People pick "every weekday at 09:00", not cron; these are the four shapes the board
// offers and the cron each one stands for. A recurrence set up some other way (Sentinel takes raw
// cron) still shows, as "Custom schedule".
public static class KanbanRepeatFrequencies
{
    public const string Daily = "daily";
    public const string Weekdays = "weekdays";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
}

// DayOfWeek: 0 Sunday .. 6 Saturday, for Weekly. DayOfMonth: 1-28, for Monthly (28 so it happens
// every month).
public sealed record KanbanRepeat(string Frequency, int Hour, int Minute, int DayOfWeek = 1, int DayOfMonth = 1);

public static partial class KanbanRepeats
{
    public static string ToCron(KanbanRepeat repeat)
    {
        var minute = Math.Clamp(repeat.Minute, 0, 59);
        var hour = Math.Clamp(repeat.Hour, 0, 23);
        return repeat.Frequency switch
        {
            KanbanRepeatFrequencies.Daily => $"{minute} {hour} * * *",
            KanbanRepeatFrequencies.Weekdays => $"{minute} {hour} * * 1-5",
            KanbanRepeatFrequencies.Weekly => $"{minute} {hour} * * {Math.Clamp(repeat.DayOfWeek, 0, 6)}",
            KanbanRepeatFrequencies.Monthly => $"{minute} {hour} {Math.Clamp(repeat.DayOfMonth, 1, 28)} * *",
            _ => throw new ArgumentException("Choose how often the card repeats.", nameof(repeat))
        };
    }

    // The board's own shapes back from cron; null for anything else.
    public static KanbanRepeat? FromCron(string? cron)
    {
        var match = CronPattern().Match(cron?.Trim() ?? string.Empty);
        if (!match.Success) return null;
        var minute = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        if (minute > 59 || hour > 23) return null;
        var day = match.Groups["day"].Value;
        var week = match.Groups["week"].Value;
        if (day == "*" && week == "*") return new KanbanRepeat(KanbanRepeatFrequencies.Daily, hour, minute);
        if (day == "*" && week == "1-5") return new KanbanRepeat(KanbanRepeatFrequencies.Weekdays, hour, minute);
        if (day == "*" && int.TryParse(week, CultureInfo.InvariantCulture, out var dayOfWeek) && dayOfWeek is >= 0 and <= 6)
            return new KanbanRepeat(KanbanRepeatFrequencies.Weekly, hour, minute, DayOfWeek: dayOfWeek);
        if (week == "*" && int.TryParse(day, CultureInfo.InvariantCulture, out var dayOfMonth) && dayOfMonth is >= 1 and <= 28)
            return new KanbanRepeat(KanbanRepeatFrequencies.Monthly, hour, minute, DayOfMonth: dayOfMonth);
        return null;
    }

    // "Every weekday at 09:00", "Every Monday at 09:00", "On the 1st of every month at 09:00".
    public static string Describe(string? cron)
    {
        if (FromCron(cron) is not { } repeat) return "Custom schedule";
        var at = $"at {repeat.Hour:00}:{repeat.Minute:00}";
        return repeat.Frequency switch
        {
            KanbanRepeatFrequencies.Daily => $"Every day {at}",
            KanbanRepeatFrequencies.Weekdays => $"Every weekday {at}",
            KanbanRepeatFrequencies.Weekly => $"Every {CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)repeat.DayOfWeek)} {at}",
            _ => $"On the {Ordinal(repeat.DayOfMonth)} of every month {at}"
        };
    }

    public static string Ordinal(int day) => day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    [GeneratedRegex(@"^(?<minute>\d{1,2}) (?<hour>\d{1,2}) (?<day>\*|\d{1,2}) \* (?<week>\*|1-5|\d)$")]
    private static partial Regex CronPattern();
}
