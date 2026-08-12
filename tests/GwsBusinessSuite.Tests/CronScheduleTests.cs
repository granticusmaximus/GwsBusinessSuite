using FluentAssertions;
using GwsBusinessSuite.Application.Automation;

namespace GwsBusinessSuite.Tests;

public sealed class CronScheduleTests
{
    [Fact]
    public void GetNextOccurrence_ShouldFindTheNextStepInterval()
    {
        var after = new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

        var next = CronSchedule.GetNextOccurrence("*/15 * * * *", after);

        next.Should().Be(new DateTimeOffset(2026, 1, 1, 0, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_ShouldFindTheNextWeeklyOccurrenceAcrossDays()
    {
        // 2026-01-01 is a Thursday; day-of-week 1 = Monday.
        var after = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        var next = CronSchedule.GetNextOccurrence("0 9 * * 1", after);

        next.Should().Be(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));
        next.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void GetNextOccurrence_ShouldSupportCommaListsAndRanges()
    {
        var after = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var next = CronSchedule.GetNextOccurrence("0,30 8-9 * * *", after);

        next.Should().Be(new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_ShouldAlwaysReturnStrictlyAfterTheGivenTime()
    {
        // Exactly on a matching minute - the next occurrence must still be in the future.
        var after = new DateTimeOffset(2026, 1, 1, 0, 15, 0, TimeSpan.Zero);

        var next = CronSchedule.GetNextOccurrence("*/15 * * * *", after);

        next.Should().Be(new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("* * * *")] // only 4 fields
    [InlineData("60 * * * *")] // minute out of range
    [InlineData("* * * * 7")] // day-of-week out of range (0-6)
    [InlineData("5-2 * * * *")] // inverted range
    [InlineData("*/0 * * * *")] // non-positive step
    [InlineData("")]
    public void Validate_ShouldRejectMalformedExpressions(string expression)
    {
        var act = () => CronSchedule.Validate(expression);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Validate_ShouldAcceptAStandardExpression()
    {
        var act = () => CronSchedule.Validate("0 9 * * 1-5");
        act.Should().NotThrow();
    }
}
