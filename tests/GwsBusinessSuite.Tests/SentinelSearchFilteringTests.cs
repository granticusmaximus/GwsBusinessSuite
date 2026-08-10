using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelSearchFilteringTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Apply_WithNoFilters_ShouldReturnEveryResult()
    {
        var results = new[] { Result("alice", Now.AddDays(-1)), Result("bob", Now.AddYears(-2)) };

        SentinelSearchFiltering.Apply(results, authorFilter: null, dateFilter: null, Now).Should().HaveCount(2);
    }

    [Fact]
    public void Apply_WithAnAuthorFilter_ShouldKeepOnlyThatAuthorsResultsCaseInsensitively()
    {
        var results = new[] { Result("Alice", Now), Result("bob", Now) };

        var filtered = SentinelSearchFiltering.Apply(results, authorFilter: "alice", dateFilter: null, Now);

        filtered.Should().ContainSingle().Which.CreatedBy.Should().Be("Alice");
    }

    [Theory]
    [InlineData(SentinelSearchFiltering.PastWeek, -3, true)]
    [InlineData(SentinelSearchFiltering.PastWeek, -10, false)]
    [InlineData(SentinelSearchFiltering.PastMonth, -20, true)]
    [InlineData(SentinelSearchFiltering.PastMonth, -40, false)]
    [InlineData(SentinelSearchFiltering.PastYear, -200, true)]
    [InlineData(SentinelSearchFiltering.PastYear, -400, false)]
    public void Apply_WithADateFilter_ShouldKeepOnlyResultsWithinThatWindow(string dateFilter, int daysAgo, bool shouldSurvive)
    {
        var results = new[] { Result("alice", Now.AddDays(daysAgo)) };

        var filtered = SentinelSearchFiltering.Apply(results, authorFilter: null, dateFilter, Now);

        filtered.Should().HaveCount(shouldSurvive ? 1 : 0);
    }

    [Fact]
    public void Apply_WithAnUnrecognizedDateFilterValue_ShouldNotFilterByDate()
    {
        var results = new[] { Result("alice", Now.AddYears(-50)) };

        SentinelSearchFiltering.Apply(results, authorFilter: null, dateFilter: "not-a-real-preset", Now).Should().HaveCount(1);
    }

    private static SentinelSearchResult Result(string createdBy, DateTimeOffset createdAt) => new(
        Guid.NewGuid(), IsDatabase: false, "Title", "Preview", "Page", Score: 1, MatchedTerms: [], createdBy, createdAt);
}
