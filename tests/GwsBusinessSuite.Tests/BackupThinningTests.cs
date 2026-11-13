using FluentAssertions;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Tests;

// Backup retention (13-11-26): all of the last two days, one a day to 30 days, one a week to 90.
public sealed class BackupThinningTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldKeepRecentAll_DailyThenWeekly_AndDropTheRest()
    {
        // Four backups a day for 100 days.
        var backups = Enumerable.Range(0, 400).Select(i => ($"b{i}", Now.AddHours(-6 * i))).ToList();

        var delete = BackupThinning.ToDelete(backups, Now, dailyDays: 30, weeklyDays: 90).ToHashSet();
        var kept = backups.Where(b => !delete.Contains(b.Item1)).ToList();

        kept.Where(b => Now - b.Item2 <= TimeSpan.FromDays(2)).Should().HaveCount(9, "everything from the last two days");
        kept.Count(b => Now - b.Item2 > TimeSpan.FromDays(2) && Now - b.Item2 <= TimeSpan.FromDays(30)).Should().BeInRange(27, 29, "one a day");
        kept.Count(b => Now - b.Item2 > TimeSpan.FromDays(30) && Now - b.Item2 <= TimeSpan.FromDays(90)).Should().BeInRange(8, 10, "one a week");
        kept.Should().NotContain(b => Now - b.Item2 > TimeSpan.FromDays(90));
        kept.Count.Should().BeLessThan(60);
    }

    [Fact]
    public void TheNewestBackup_ShouldAlwaysBeKept_EvenWhenOld() =>
        BackupThinning.ToDelete([("only", Now.AddDays(-200))], Now, 30, 90).Should().BeEmpty();
}
