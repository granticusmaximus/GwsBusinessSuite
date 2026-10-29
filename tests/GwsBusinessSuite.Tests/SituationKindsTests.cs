using FluentAssertions;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

public sealed class SituationKindsTests
{
    [Fact]
    public void Classify_ShouldPickTheKindWithTheClosestExample_OrOtherWhenNothingIsClose()
    {
        var examples = new Dictionary<string, IReadOnlyList<float[]>>
        {
            [SituationKinds.Transport] = [[1f, 0f, 0f]],
            [SituationKinds.Sport] = [[0f, 1f, 0f]]
        };

        SituationKinds.Classify([9f, 2f, 0f], examples).Should().Be(SituationKinds.Transport, "a sum is compared by direction");
        SituationKinds.Classify([1f, 3f, 0f], examples).Should().Be(SituationKinds.Sport);
        SituationKinds.Classify([0f, 0f, 5f], examples).Should().Be(SituationKinds.Other, "nothing reaches the minimum score");
    }

    [Theory]
    [InlineData("Two high-speed trains collide near Madrid", SituationKinds.Transport)]
    [InlineData("Ebola outbreak tops 10,000 cases", SituationKinds.Health)]
    [InlineData("Cyclone forces evacuations; floods expected", SituationKinds.Disaster)]
    [InlineData("Drone attack on military base", SituationKinds.Conflict)]
    [InlineData("Arsenal stunned in League Cup match", SituationKinds.Sport)]
    [InlineData("Dolores Huerta, labor leader, dies at 96", SituationKinds.Other)]
    public void FromWords_ShouldStandInWhenTheModelIsDown(string headline, string kind)
    {
        SituationKinds.FromWords(headline).Should().Be(kind);
    }

    [Fact]
    public void Surge_ShouldNeedCoverageToDoubleWithSeveralNewOutlets()
    {
        SituationSurge.IsSurging(8, 3).Should().BeTrue();
        SituationSurge.IsSurging(5, 0).Should().BeTrue("a story that's new this hour");
        SituationSurge.IsSurging(6, 3).Should().BeFalse("only three new outlets");
        SituationSurge.IsSurging(30, 20).Should().BeFalse("big, but not doubling");
    }
}
