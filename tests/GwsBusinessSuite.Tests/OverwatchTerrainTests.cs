using FluentAssertions;
using GwsBusinessSuite.Web;

namespace GwsBusinessSuite.Tests;

// Overwatch's key-less terrain proxy passes on only real tile coordinates (13-11-26).
public sealed class OverwatchTerrainTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(2, 3, 3, true)]
    [InlineData(2, 4, 0, false)]
    [InlineData(14, 16383, 16383, true)]
    [InlineData(15, 0, 0, false)]
    [InlineData(3, -1, 0, false)]
    public void IsTile_ShouldAcceptOnlyRealTilesUpToZoom14(int z, int x, int y, bool ok) =>
        OverwatchTerrain.IsTile(z, x, y).Should().Be(ok);

    [Fact]
    public void TileUrl_ShouldPointAtTheOpenTerrariumTiles() =>
        OverwatchTerrain.TileUrl(2, 1, 1).Should().Be("https://s3.amazonaws.com/elevation-tiles-prod/terrarium/2/1/1.png");
}
