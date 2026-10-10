using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelDateExpressionsTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    // Friday 9 October 2026, 11:30pm in New York (03:30 UTC on the 10th) - "today" must be the 9th.
    private static readonly DateTimeOffset LateFriday = new(2026, 10, 10, 3, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("today", "2026-10-09")]
    [InlineData("@Tomorrow", "2026-10-10")]
    [InlineData("yesterday", "2026-10-08")]
    [InlineData("next week", "2026-10-16")]
    [InlineData("in 3 days", "2026-10-12")]
    [InlineData("in 2 weeks", "2026-10-23")]
    [InlineData("friday", "2026-10-16")]
    [InlineData("next Friday", "2026-10-16")]
    [InlineData("mon", "2026-10-12")]
    [InlineData("Oct 16", "2026-10-16")]
    [InlineData("october 1", "2027-10-01")]
    [InlineData("Feb 29, 2028", "2028-02-29")]
    [InlineData("2026-12-31", "2026-12-31")]
    public void TryParse_ShouldResolveWholeDaysInTheGivenZone(string phrase, string expected)
    {
        SentinelDateExpressions.TryParse(phrase, LateFriday, NewYork, out var result, out _).Should().BeTrue();

        result!.Date.ToString("yyyy-MM-dd").Should().Be(expected);
        result.Time.Should().BeNull();
        result.MentionValue.Should().Be(expected);
        result.Label.Should().Be(DateOnly.Parse(expected).ToString("dd-MM-yy"));
    }

    [Theory]
    [InlineData("next friday 3pm", "2026-10-16T19:00:00Z", "16-10-26 15:00")]
    [InlineData("tomorrow at 9:30am", "2026-10-10T13:30:00Z", "10-10-26 09:30")]
    [InlineData("2026-12-01 15:00", "2026-12-01T20:00:00Z", "01-12-26 15:00")]
    [InlineData("3pm", "2026-10-09T19:00:00Z", "09-10-26 15:00")]
    [InlineData("Oct 16 12am", "2026-10-16T04:00:00Z", "16-10-26 00:00")]
    public void TryParse_WithATime_ShouldStoreTheUtcInstant(string phrase, string expectedValue, string expectedLabel)
    {
        SentinelDateExpressions.TryParse(phrase, LateFriday, NewYork, out var result, out _).Should().BeTrue();

        result!.MentionValue.Should().Be(expectedValue);
        result.Label.Should().Be(expectedLabel);
    }

    [Fact]
    public void TryParse_ShouldRefuseTimesTheClocksSkipOrRepeat()
    {
        var march = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        SentinelDateExpressions.TryParse("2026-03-08 2:30am", march, NewYork, out _, out var skipped).Should().BeFalse();
        skipped.Should().Contain("doesn't exist");
        SentinelDateExpressions.TryParse("2026-11-01 1:30am", march, NewYork, out _, out var repeated).Should().BeFalse();
        repeated.Should().Contain("twice");
        // Either side of the change is fine and gets the right offset.
        SentinelDateExpressions.TryParse("2026-03-08 3:30am", march, NewYork, out var after, out _).Should().BeTrue();
        after!.MentionValue.Should().Be("2026-03-08T07:30:00Z");
    }

    [Theory]
    [InlineData("")]
    [InlineData("someday")]
    [InlineData("next")]
    [InlineData("in 0 days")]
    [InlineData("in 500 days")]
    [InlineData("feb 30")]
    [InlineData("today 3")]
    [InlineData("today 13pm")]
    [InlineData("today 24:00")]
    [InlineData("today 10:75")]
    [InlineData("fr")]
    public void TryParse_ShouldRejectUnclearOrImpossiblePhrases(string phrase)
    {
        SentinelDateExpressions.TryParse(phrase, LateFriday, NewYork, out var result, out _).Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void MentionValues_ShouldReadBack_IncludingTheOlderDayOnlyFormat()
    {
        SentinelDateExpressions.TryReadMentionValue("2026-07-21", NewYork, out var day, out var noInstant).Should().BeTrue();
        day.Should().Be(new DateOnly(2026, 7, 21));
        noInstant.Should().BeNull();

        SentinelDateExpressions.TryReadMentionValue("2026-10-10T03:30:00Z", NewYork, out var localDay, out var instant).Should().BeTrue();
        localDay.Should().Be(new DateOnly(2026, 10, 9));
        instant.Should().Be(LateFriday);

        SentinelDateExpressions.TryReadMentionValue("not a date", NewYork, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ReminderInstant_ForAWholeDay_ShouldBeNineInTheMorningLocally()
    {
        SentinelDateExpressions.ReminderInstant(new DateOnly(2026, 12, 1), null, NewYork)
            .Should().Be(new DateTimeOffset(2026, 12, 1, 14, 0, 0, TimeSpan.Zero));
        SentinelDateExpressions.ReminderInstant(new DateOnly(2026, 7, 1), null, NewYork)
            .Should().Be(new DateTimeOffset(2026, 7, 1, 13, 0, 0, TimeSpan.Zero));
    }
}
