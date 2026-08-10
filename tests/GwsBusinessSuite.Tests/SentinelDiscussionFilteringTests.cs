using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelDiscussionFilteringTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThreadAuthor_ShouldBeWhoeverWroteTheFirstComment()
    {
        var discussion = Discussion(Now, ("alice", Now), ("bob", Now.AddMinutes(5)));

        SentinelDiscussionFiltering.ThreadAuthor(discussion).Should().Be("alice");
    }

    [Fact]
    public void Apply_WithAnAuthorFilter_ShouldMatchOnlyTheThreadStarterNotLaterRepliers()
    {
        var discussions = new[]
        {
            Discussion(Now, ("alice", Now)),
            Discussion(Now, ("bob", Now), ("alice", Now.AddMinutes(5))) // alice only replied, didn't start this one
        };

        var filtered = SentinelDiscussionFiltering.Apply(discussions, authorFilter: "alice", dateFilter: null, Now);

        filtered.Should().ContainSingle();
        SentinelDiscussionFiltering.ThreadAuthor(filtered[0]).Should().Be("alice");
    }

    [Fact]
    public void Apply_WithADateFilter_ShouldKeepOnlyThreadsCreatedWithinTheWindow()
    {
        var discussions = new[]
        {
            Discussion(Now.AddDays(-2), ("alice", Now.AddDays(-2))),
            Discussion(Now.AddYears(-1), ("alice", Now.AddYears(-1)))
        };

        var filtered = SentinelDiscussionFiltering.Apply(discussions, authorFilter: null, SentinelSearchFiltering.PastWeek, Now);

        filtered.Should().ContainSingle().Which.CreatedAt.Should().Be(Now.AddDays(-2));
    }

    private static SentinelDiscussionView Discussion(DateTimeOffset createdAt, params (string Author, DateTimeOffset At)[] comments) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        BlockId: null,
        IsResolved: false,
        ResolvedAt: null,
        ResolvedBy: null,
        createdAt,
        comments.Select(comment => new SentinelDiscussionCommentView(Guid.NewGuid(), null, "Body", comment.Author, comment.At, [])).ToList());
}
