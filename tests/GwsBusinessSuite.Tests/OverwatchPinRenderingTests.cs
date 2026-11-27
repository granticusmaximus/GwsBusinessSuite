using FluentAssertions;

namespace GwsBusinessSuite.Tests;

public sealed class OverwatchPinRenderingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    // 18-11-26: the World News panel names the subtype. A stale editor buffer saved over the file
    // removed it on 19-11-26 with every test still green. Since 26-11-26 (the Mac look) a news pin is
    // the Mac's news disc, whose symbol comes from the icon font.
    [Fact]
    public void WorldNewsPins_ShouldBeTheMacNewsDisc_AndThePanelTheSubtype()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));

        source.Should().Contain("const billboard = Object.assign(macPin('setWorldNews', signal), { disableDepthTestDistance: 0 });")
            .And.Contain("whenIconFontReady(",
                "pins drawn before the icon font loads must be redrawn with their symbols")
            .And.Contain("await loadMacIconFont();", "the globe waits for the icon font before drawing pins");
        source.Should().Contain("signal.subtype && signal.tagLabel",
            "the World News panel shows the story's subtype");
    }

    // 19-11-26: the Mac app crashed zooming in on Georgia. Building MapKit's pin groups ourselves
    // (mapView:clusterAnnotationForMemberAnnotations:) gave its quad trie a nil annotation on macOS 27;
    // the app only labels the groups MapKit makes.
    [Fact]
    public void MacMap_ShouldLabelMapKitsOwnGroups_NotBuildThem()
    {
        var bridge = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacNativeOverwatchBridge.cs"));
        bridge.Should().Contain("view.ClusteringIdentifier = null;",
            "MapKit's grouping itself crashed the app zooming in (19-11-26, twice); pins don't join groups")
            .And.NotContain("view.ClusteringIdentifier = native.Operation");
        bridge.Should().NotContain("override MKClusterAnnotation CreateClusterAnnotation")
            .And.NotContain("new MKClusterAnnotation(")
            .And.Contain("LabelCluster(annotation);");
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
        // Since 26-11-26 the camera, incident and World News groups share one style: the Mac's count disc,
        // with the count drawn into the image (no separate label to be covered).
        System.Text.RegularExpressions.Regex.Matches(source, @"styleMacCluster\(clusteredEntities, cluster\);")
            .Should().HaveCount(4, "camera, incident, World News and conflict clusters use the Mac's count disc");
        var style = source[source.IndexOf("function styleMacCluster(", StringComparison.Ordinal)..];
        style = style[..style.IndexOf("\n    }", StringComparison.Ordinal)];
        style.Should().Contain("cluster.point.show = false;", "Cesium's own point is hidden")
            .And.Contain("cluster.billboard.disableDepthTestDistance = 0;", "group discs stay occluded by the globe")
            .And.Contain("cluster.label.show = false;", "the count is part of the disc");
        source.Should().Contain("viewer.scene.drillPick(movement.position, 32)")
            .And.Contain("drill.map(pickIdentifier).find(isActionable)",
                "a decorative primitive on top of a pin must not prevent the actionable object from opening");
    }

    // 19-11-26: 26 outlets running one wire story are one source (SourceIndependence), and the panel
    // says how many independent sources there are. Pins were sized by that count until the Mac look
    // (26-11-26), where every news pin is the Mac's fixed-size disc.
    [Fact]
    public void WorldNewsPanel_ShouldCountIndependentSources()
    {
        var js = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        var map = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Infrastructure/Services/WorldNewsMapService.cs"));
        js.Should().Contain("' INDEPENDENT'");
        map.Should().Contain("SourceIndependence.Assess(events.SelectMany(e => store.ArticlesForEvent(e.Id)).ToList()).Independent");
    }
}
