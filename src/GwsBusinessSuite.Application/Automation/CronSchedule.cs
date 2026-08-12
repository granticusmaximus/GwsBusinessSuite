namespace GwsBusinessSuite.Application.Automation;

// A minimal, dependency-free 5-field cron evaluator (minute hour day-of-month month
// day-of-week) for core.scheduleTrigger's cron mode - built in-house rather than pulling in a
// cron library, matching this codebase's existing preference for small clean-room tools over
// new dependencies (see docs/WORKFLOW_AUTOMATION.md's "clean-room" framing). Deliberately
// supports only the common subset: '*', a number, comma lists, 'N-M' ranges, and '*/S' or
// 'N-M/S' steps - no named months/weekdays, no 'L'/'W'/'#'. Fields are numeric-only by design;
// an author who needs more should compose multiple simpler expressions or use the plain
// interval mode instead.
public static class CronSchedule
{
    private static readonly (int Min, int Max)[] FieldRanges =
    [
        (0, 59), // minute
        (0, 23), // hour
        (1, 31), // day of month
        (1, 12), // month
        (0, 6)   // day of week (0 = Sunday)
    ];

    private const int MaxLookaheadMinutes = 60 * 24 * 366 * 4; // ~4 years, bounds a pathological/impossible expression

    public static void Validate(string expression)
    {
        ParseFields(expression);
    }

    // Returns the first occurrence strictly after `after`, minute-resolution. Walking
    // minute-by-minute is simple and provably correct (no edge cases around DST/month-length
    // arithmetic to get wrong) and cheap enough for a scheduler that only needs to compute this
    // once per firing, not once per second.
    public static DateTimeOffset GetNextOccurrence(string expression, DateTimeOffset after)
    {
        var fields = ParseFields(expression);
        var candidate = after.AddMinutes(1);
        candidate = new DateTimeOffset(
            candidate.Year, candidate.Month, candidate.Day, candidate.Hour, candidate.Minute, 0, candidate.Offset);

        for (var step = 0; step < MaxLookaheadMinutes; step++)
        {
            if (Matches(fields, candidate)) return candidate;
            candidate = candidate.AddMinutes(1);
        }
        throw new InvalidOperationException($"Cron expression '{expression}' has no matching occurrence within 4 years.");
    }

    private static bool Matches(HashSet<int>[] fields, DateTimeOffset value) =>
        fields[0].Contains(value.Minute)
        && fields[1].Contains(value.Hour)
        && fields[2].Contains(value.Day)
        && fields[3].Contains(value.Month)
        && fields[4].Contains((int)value.DayOfWeek);

    private static HashSet<int>[] ParseFields(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new FormatException("Cron expression is required.");
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            throw new FormatException("Cron expression must have exactly 5 space-separated fields: minute hour day-of-month month day-of-week.");

        var fields = new HashSet<int>[5];
        for (var i = 0; i < 5; i++)
        {
            fields[i] = ParseField(parts[i], FieldRanges[i].Min, FieldRanges[i].Max);
        }
        return fields;
    }

    private static HashSet<int> ParseField(string field, int min, int max)
    {
        var values = new HashSet<int>();
        foreach (var segment in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var (range, step) = SplitStep(segment);
            var (rangeMin, rangeMax) = range == "*" ? (min, max) : ParseRange(range, min, max);
            for (var value = rangeMin; value <= rangeMax; value += step) values.Add(value);
        }
        if (values.Count == 0)
            throw new FormatException($"Cron field '{field}' did not resolve to any value.");
        return values;
    }

    private static (string Range, int Step) SplitStep(string segment)
    {
        var slashIndex = segment.IndexOf('/');
        if (slashIndex < 0) return (segment, 1);
        var range = segment[..slashIndex];
        if (!int.TryParse(segment[(slashIndex + 1)..], out var step) || step <= 0)
            throw new FormatException($"Cron step '{segment}' must be a positive integer after '/'.");
        return (range, step);
    }

    private static (int Min, int Max) ParseRange(string range, int fieldMin, int fieldMax)
    {
        var dashIndex = range.IndexOf('-');
        if (dashIndex < 0)
        {
            if (!int.TryParse(range, out var single)) throw new FormatException($"Cron value '{range}' is not a number.");
            EnsureInRange(single, fieldMin, fieldMax, range);
            return (single, single);
        }
        if (!int.TryParse(range[..dashIndex], out var start) || !int.TryParse(range[(dashIndex + 1)..], out var end))
            throw new FormatException($"Cron range '{range}' is not two numbers separated by '-'.");
        EnsureInRange(start, fieldMin, fieldMax, range);
        EnsureInRange(end, fieldMin, fieldMax, range);
        if (start > end) throw new FormatException($"Cron range '{range}' starts after it ends.");
        return (start, end);
    }

    private static void EnsureInRange(int value, int min, int max, string segment)
    {
        if (value < min || value > max)
            throw new FormatException($"Cron value '{segment}' is out of range ({min}-{max}).");
    }
}
