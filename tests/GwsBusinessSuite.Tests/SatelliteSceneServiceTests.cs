using FluentAssertions;
using GwsBusinessSuite.Application.Overwatch;

namespace GwsBusinessSuite.Tests;

// Overwatch satellite scenes (Planetary Computer), 13-11-26.
public sealed class SatelliteSceneServiceTests
{
    [Fact]
    public void Parse_ShouldKeepTheClearestSceneEachDay_NewestFirst_WithATileTemplate()
    {
        var scenes = SatelliteSceneService.Parse("""
            {"features":[
              {"id":"S2C_A_20261113","properties":{"datetime":"2026-11-13T08:32:11Z","eo:cloud_cover":9.0}},
              {"id":"S2C_B_20261113","properties":{"datetime":"2026-11-13T08:32:15Z","eo:cloud_cover":1.5}},
              {"id":"S2B_C_20261108","properties":{"datetime":"2026-11-08T08:41:49Z","eo:cloud_cover":13.7}},
              {"id":"bad id/../x","properties":{"datetime":"2026-11-01T08:00:00Z","eo:cloud_cover":0}}
            ]}
            """);

        scenes.Select(scene => scene.Id).Should().Equal("S2C_B_20261113", "S2B_C_20261108");
        scenes[0].TileUrlTemplate.Should().StartWith("https://planetarycomputer.microsoft.com/api/data/v1/item/tiles/WebMercatorQuad/{z}/{x}/{y}@1x?collection=sentinel-2-l2a&item=S2C_B_20261113");
    }
}
