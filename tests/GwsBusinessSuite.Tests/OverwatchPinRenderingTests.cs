using FluentAssertions;

namespace GwsBusinessSuite.Tests;

public sealed class OverwatchPinRenderingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void GlobePins_ShouldRemainDepthTested_AndClusterCountsShouldNotBeCovered()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));

        source.Should().NotContain("disableDepthTestDistance = Number.POSITIVE_INFINITY",
            "far-side markers must be occluded by the globe instead of bleeding through it");
        System.Text.RegularExpressions.Regex.Matches(source, @"cluster\.point\.show\s*=\s*false")
            .Should().HaveCount(3,
                "camera, incident, and World News clusters must hide Cesium's point so their count label stays visible");
    }
}
