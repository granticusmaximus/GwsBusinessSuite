namespace GwsBusinessSuite.Web.Services;

// Which backups to keep (13-11-26). A backup is taken every few hours and before every deploy, so a
// flat 30 days kept 266 of them (60 GB). Now: every backup from the last two days, the newest of each
// day up to DailyDays, the newest of each week up to WeeklyDays, nothing older. The newest backup is
// always kept. Old-style sentinel-backup-*.zip archives follow the same rule.
public static class BackupThinning
{
    public static readonly TimeSpan KeepAll = TimeSpan.FromDays(2);

    public static IReadOnlyList<string> ToDelete(IEnumerable<(string Path, DateTimeOffset At)> backups, DateTimeOffset now, int dailyDays, int weeklyDays)
    {
        var ordered = backups.OrderByDescending(backup => backup.At).ToList();
        if (ordered.Count == 0) return [];
        var keep = new HashSet<string>(StringComparer.Ordinal) { ordered[0].Path };
        var days = new HashSet<DateOnly>();
        var weeks = new HashSet<(int Year, int Week)>();
        foreach (var (path, at) in ordered)
        {
            var age = now - at;
            if (age <= KeepAll) keep.Add(path);
            else if (age <= TimeSpan.FromDays(dailyDays))
            {
                if (days.Add(DateOnly.FromDateTime(at.UtcDateTime))) keep.Add(path);
            }
            else if (age <= TimeSpan.FromDays(weeklyDays))
            {
                var week = (System.Globalization.ISOWeek.GetYear(at.UtcDateTime), System.Globalization.ISOWeek.GetWeekOfYear(at.UtcDateTime));
                if (weeks.Add(week)) keep.Add(path);
            }
        }
        return ordered.Where(backup => !keep.Contains(backup.Path)).Select(backup => backup.Path).ToList();
    }
}
