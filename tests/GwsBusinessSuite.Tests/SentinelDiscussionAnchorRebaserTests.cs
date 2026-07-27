using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelDiscussionAnchorRebaserTests
{
    [Fact]
    public void Rebase_ShouldFollowSelectedTextAfterLargeSurroundingInsert()
    {
        var anchor = new SentinelDiscussionAnchor("important phrase", 7, 23);
        var previous = "Before important phrase after";
        var current = $"{new string('x', 300)} Before important phrase after";

        var rebased = SentinelDiscussionAnchorRebaser.Rebase(anchor, previous, current);

        rebased.Should().NotBeNull();
        current[rebased!.Start..rebased.End].Should().Be("important phrase");
    }

    [Fact]
    public void Rebase_ShouldChooseTheOccurrenceClosestToThePreviousPosition()
    {
        var anchor = new SentinelDiscussionAnchor("repeat", 20, 26);

        var rebased = SentinelDiscussionAnchorRebaser.Rebase(
            anchor,
            "prefix repeat near repeat",
            "repeat prefix padding repeat ending repeat");

        rebased.Should().Be(new SentinelDiscussionAnchor("repeat", 22, 28));
    }

    [Fact]
    public void Rebase_ShouldReturnNullWhenSelectedTextNoLongerExists()
    {
        SentinelDiscussionAnchorRebaser.Rebase(
                new SentinelDiscussionAnchor("removed", 0, 7),
                "removed",
                "replacement")
            .Should()
            .BeNull();
    }
}
