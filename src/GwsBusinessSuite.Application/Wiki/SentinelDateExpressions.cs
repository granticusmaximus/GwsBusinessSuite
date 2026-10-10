using System.Globalization;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Application.Wiki;

// A date (and optional time) written in Sentinel's @ menu, e.g. "@today", "@next Friday 3pm",
// "@in 3 days", "@Oct 16 9:30am" or "@2026-10-16 15:00".
//
// Stored in a "datemention:" link value: "yyyy-MM-dd" for a whole day (the format date mentions
// have always used) or a UTC instant "yyyy-MM-ddTHH:mm:ssZ" when a time was given. The chip's
// saved text is the absolute Label; how far away it is ("today", "in 2 days") is worked out
// when it's shown and never saved.
public sealed record SentinelDateExpression(DateOnly Date, TimeOnly? Time, DateTimeOffset? InstantUtc, string Label)
{
    public string MentionValue => InstantUtc is { } instant
        ? instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        : Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public static partial class SentinelDateExpressions
{
    // A whole-day mention's reminder fires at this local time on that day.
    public static readonly TimeOnly DefaultReminderTime = new(9, 0);

    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;
    private static readonly string[] MonthAbbreviations = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames;

    // Parses the phrase relative to "now" in the given time zone. Returns false - with a reason
    // where useful - for anything unrecognised or ambiguous rather than guessing.
    public static bool TryParse(string? phrase, DateTimeOffset now, TimeZoneInfo zone, out SentinelDateExpression? result, out string? problem)
    {
        result = null;
        problem = null;
        var text = Regex.Replace((phrase ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant(), @"\s+", " ");
        if (text.Length == 0) return false;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        // Split off a trailing time ("3pm", "3:30 pm", "15:00", optionally after "at").
        TimeOnly? time = null;
        // A bare trailing number ("oct 16") is part of the date, not a time.
        var timeMatch = TimeSuffix().Match(text);
        if (timeMatch.Success && (timeMatch.Groups["m"].Success || timeMatch.Groups["ampm"].Success))
        {
            if (!TryParseTime(timeMatch, out var parsedTime, out problem)) return false;
            time = parsedTime;
            text = text[..timeMatch.Index].Trim();
            if (text.EndsWith(" at", StringComparison.Ordinal)) text = text[..^3].Trim();
            if (text.Length == 0) text = "today";
        }

        if (!TryParseDay(text, today, out var date, out problem)) return false;

        DateTimeOffset? instant = null;
        if (time is { } wallTime)
        {
            var local = date.ToDateTime(wallTime, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
            {
                problem = "That time doesn't exist on that day (the clocks go forward).";
                return false;
            }
            if (zone.IsAmbiguousTime(local))
            {
                problem = "That time happens twice on that day (the clocks go back) - pick another time.";
                return false;
            }
            instant = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        }

        result = new SentinelDateExpression(date, time, instant, FormatLabel(date, time));
        return true;
    }

    // Reads a stored "datemention:" value back. A whole day comes back with no instant.
    public static bool TryReadMentionValue(string? value, TimeZoneInfo zone, out DateOnly date, out DateTimeOffset? instantUtc)
    {
        date = default;
        instantUtc = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        if (DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
        {
            instantUtc = instant;
            date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
            return true;
        }
        return false;
    }

    // When a reminder on this mention fires: its instant, or DefaultReminderTime on a whole day.
    public static DateTimeOffset ReminderInstant(DateOnly date, DateTimeOffset? instantUtc, TimeZoneInfo zone)
    {
        if (instantUtc is { } instant) return instant;
        var local = date.ToDateTime(DefaultReminderTime, DateTimeKind.Unspecified);
        // 9am is never inside a DST transition in practice; fall back to the standard offset anyway.
        return new DateTimeOffset(local, zone.IsInvalidTime(local) ? zone.BaseUtcOffset : zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static bool TryParseDay(string text, DateOnly today, out DateOnly date, out string? problem)
    {
        problem = null;
        date = today;
        switch (text)
        {
            case "today": return true;
            case "tomorrow": date = today.AddDays(1); return true;
            case "yesterday": date = today.AddDays(-1); return true;
            case "next week": date = today.AddDays(7); return true;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;

        var relative = InRelative().Match(text);
        if (relative.Success)
        {
            var count = int.Parse(relative.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (count is < 1 or > 366)
            {
                problem = "Use a number between 1 and 366.";
                date = default;
                return false;
            }
            date = relative.Groups["unit"].Value.StartsWith('w') ? today.AddDays(7 * count) : today.AddDays(count);
            return true;
        }

        // "friday" and "next friday" both mean the next Friday after today (1-7 days away).
        var weekdayText = text.StartsWith("next ", StringComparison.Ordinal) ? text[5..] : text;
        if (TryWeekday(weekdayText, out var weekday))
        {
            var days = ((int)weekday - (int)today.DayOfWeek + 7) % 7;
            date = today.AddDays(days == 0 ? 7 : days);
            return true;
        }

        var monthDay = MonthDay().Match(text);
        if (monthDay.Success && TryMonth(monthDay.Groups["month"].Value, out var month))
        {
            var day = int.Parse(monthDay.Groups["day"].Value, CultureInfo.InvariantCulture);
            var year = monthDay.Groups["year"].Success ? int.Parse(monthDay.Groups["year"].Value, CultureInfo.InvariantCulture) : today.Year;
            if (day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                problem = "That day isn't in that month.";
                date = default;
                return false;
            }
            date = new DateOnly(year, month, day);
            // Without a year, a date already past this year means next year's.
            if (!monthDay.Groups["year"].Success && date < today) date = date.AddYears(1);
            return true;
        }

        date = default;
        return false;
    }

    private static bool TryParseTime(Match match, out TimeOnly time, out string? problem)
    {
        time = default;
        problem = null;
        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var meridiem = match.Groups["ampm"].Success ? match.Groups["ampm"].Value : null;
        if (minute > 59)
        {
            problem = "Minutes go up to 59.";
            return false;
        }
        if (meridiem is null)
        {
            // 24-hour time; a bare "3" never reaches here (see TryParse).
            if (hour > 23)
            {
                problem = "Hours go up to 23.";
                return false;
            }
        }
        else
        {
            if (hour is < 1 or > 12)
            {
                problem = "With am/pm, use an hour from 1 to 12.";
                return false;
            }
            hour = hour % 12 + (meridiem.StartsWith('p') ? 12 : 0);
        }
        time = new TimeOnly(hour, minute);
        return true;
    }

    private static bool TryWeekday(string text, out DayOfWeek weekday)
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var name = day.ToString().ToLowerInvariant();
            if (text == name || (text.Length >= 3 && name.StartsWith(text, StringComparison.Ordinal) && text.Length <= name.Length))
            {
                weekday = day;
                return true;
            }
        }
        weekday = default;
        return false;
    }

    private static bool TryMonth(string text, out int month)
    {
        for (var index = 0; index < 12; index++)
        {
            if (text == MonthNames[index].ToLowerInvariant() || text == MonthAbbreviations[index].ToLowerInvariant()
                || (text == "sept" && index == 8))
            {
                month = index + 1;
                return true;
            }
        }
        month = 0;
        return false;
    }

    // "16-10-26" or "16-10-26 15:00" (GwsDateFormat): absolute, so it reads correctly forever.
    private static string FormatLabel(DateOnly date, TimeOnly? time) =>
        time is { } t ? $"{date.ToGwsDate()} {t.ToGwsTime()}" : date.ToGwsDate();

    [GeneratedRegex(@"(?:^|\s)(?<h>\d{1,2})(?::(?<m>\d{2}))?\s?(?<ampm>am|pm|a\.m\.|p\.m\.)?$")]
    private static partial Regex TimeSuffix();

    [GeneratedRegex(@"^in (?<n>\d{1,3}) (?<unit>days?|weeks?)$")]
    private static partial Regex InRelative();

    [GeneratedRegex(@"^(?<month>[a-z]{3,9})\.? (?<day>\d{1,2})(?:,? (?<year>\d{4}))?$")]
    private static partial Regex MonthDay();
}
