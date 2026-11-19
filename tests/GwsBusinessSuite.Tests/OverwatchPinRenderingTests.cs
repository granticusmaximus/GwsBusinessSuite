using FluentAssertions;

namespace GwsBusinessSuite.Tests;

public sealed class OverwatchPinRenderingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    // 18-11-26: World News pins carry their story's icon and the panel names the subtype. A stale
    // editor buffer saved over the file removed both on 19-11-26 with every test still green.
    [Fact]
    public void WorldNewsPins_ShouldCarryTheStoryIcon_AndThePanelTheSubtype()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));

        source.Should().Contain("function iconPinImage(name)")
            .And.Contain("image: iconsReady ? iconPinImage(signal.icon) : pinBillboardImage",
                "World News pins draw the story's Bootstrap icon once the icon font has loaded")
            .And.Contain("whenIconFontReady(",
                "pins drawn before the icon font loads must be redrawn with their icons");
        source.Should().Contain("signal.subtype && signal.tagLabel",
            "the World News panel shows the story's subtype");
    }

    // 19-11-26: every hazard and conflict window opens its World News briefing.
    [Fact]
    public void HazardWindows_ShouldOfferTheWorldNewsStory()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        source.Should().Contain("tg-hazard-world-news\">OPEN IN WORLD NEWS</button>")
            .And.Contain("window.location.assign('/admin/world-news/brief?' + params.toString())",
                "the item's briefing: what it is, the executive summary, the report and the coverage");
    }

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
        System.Text.RegularExpressions.Regex.Matches(source, @"cluster\.billboard\.disableDepthTestDistance\s*=\s*0")
            .Should().HaveCount(3,
                "camera, incident, and World News cluster markers must remain occluded by the globe");
        System.Text.RegularExpressions.Regex.Matches(source, @"cluster\.label\.disableDepthTestDistance\s*=\s*0")
            .Should().HaveCount(3,
                "camera, incident, and World News count labels must use the same globe depth test");
        source.Should().Contain("viewer.scene.drillPick(movement.position, 32)")
            .And.Contain("drill.map(pickIdentifier).find(isActionable)",
                "a decorative primitive on top of a pin must not prevent the actionable object from opening");
    }
}
