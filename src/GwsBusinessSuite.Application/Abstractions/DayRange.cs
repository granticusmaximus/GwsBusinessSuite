namespace GwsBusinessSuite.Application.Abstractions;

// The last N days as the admin pages show them - in the server's time zone (TZ in .env) - for
// the sparklines on dashboard tiles (T5, 05-11-26). StartUnixSeconds lets a query filter in SQL
// (SQLite can't compare DateTimeOffset); Count buckets the matching times into one count per day,
// oldest first, today last.
public sealed record DayRange(DateOnly FirstDay, int Days, TimeZoneInfo Zone)
{
    public static DayRange Last(int days, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return new DayRange(today.AddDays(1 - days), days, zone);
    }

    // Midnight at the start of FirstDay in Zone, as an instant.
    public DateTimeOffset Start
    {
        get
        {
            var local = FirstDay.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
        }
    }

    public long StartUnixSeconds => Start.ToUnixTimeSeconds();

    public IReadOnlyList<int> Count(IEnumerable<DateTimeOffset> times)
    {
        var counts = new int[Days];
        foreach (var time in times)
        {
            var index = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, Zone).DateTime).DayNumber - FirstDay.DayNumber;
            if (index >= 0 && index < Days) counts[index]++;
        }
        return counts;
    }

    public IReadOnlyList<int> CountUnixSeconds(IEnumerable<long> seconds) =>
        Count(seconds.Select(DateTimeOffset.FromUnixTimeSeconds));
}
