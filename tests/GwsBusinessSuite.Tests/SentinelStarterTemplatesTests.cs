using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelStarterTemplatesTests
{
    [Fact]
    public void All_ShouldOfferAtLeastAFewNonEmptyStarterTemplatesWithUniqueKeys()
    {
        SentinelStarterTemplates.All.Count.Should().BeGreaterThanOrEqualTo(3);
        SentinelStarterTemplates.All.Select(template => template.Key).Should().OnlyHaveUniqueItems();
        SentinelStarterTemplates.All.Should().OnlyContain(template => template.Blocks.Count > 0 && template.Title.Length > 0);
    }

    [Fact]
    public void Find_ShouldBeCaseInsensitiveAndReturnNullForAnUnknownKey()
    {
        SentinelStarterTemplates.Find("MEETING-NOTES").Should().NotBeNull();
        SentinelStarterTemplates.Find("not-a-real-template").Should().BeNull();
    }

    [Fact]
    public void EveryTemplatesBlocks_ShouldRoundTripThroughWikiBlockJson()
    {
        foreach (var template in SentinelStarterTemplates.All)
        {
            var roundTripped = WikiBlockJson.ParseBlocks(WikiBlockJson.Serialize(template.Blocks));
            roundTripped.Should().HaveCount(template.Blocks.Count);
            roundTripped.Select(block => block.PlainText).Should().Equal(template.Blocks.Select(block => block.PlainText));
        }
    }
}
