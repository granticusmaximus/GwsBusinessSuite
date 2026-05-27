using FluentAssertions;
using GwsBusinessSuite.Application.CmsKnowledge;

namespace GwsBusinessSuite.Tests;

public sealed class CmsKnowledgeServiceTests
{
    [Fact]
    public async Task ListSourcesAsync_ShouldReturnCleanRoomSources()
    {
        var service = new CmsKnowledgeService();

        var sources = await service.ListSourcesAsync();

        sources.Should().HaveCountGreaterThan(1);
        sources.Should().Contain(x => x.Key == "wp-clean-room");
        sources.Should().Contain(x => x.Key == "elementor-clean-room");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnRankedMatches()
    {
        var service = new CmsKnowledgeService();

        var results = await service.SearchAsync("responsive style breakpoint", take: 3);

        results.Should().NotBeEmpty();
        results[0].Capability.Should().ContainEquivalentOf("Responsive");
        results.Should().OnlyContain(x => x.Score > 0);
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenQueryIsBlank()
    {
        var service = new CmsKnowledgeService();

        var results = await service.SearchAsync("   ");

        results.Should().BeEmpty();
    }
}
