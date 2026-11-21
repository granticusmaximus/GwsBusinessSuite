using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// Night Ops x Apple, combined (20-11-26): the kit's marker shapes on the web globe, target brackets,
// thermal tint, line of sight and the quality setting. Source guards; the browser checks are in
// OverwatchNightOpsBrowserTests.
public sealed class OverwatchNightOpsMarkerTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    private static string Script => File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));

    [Fact]
    public void Markers_ShouldComeFromOneCachedGlyphSet()
    {
        var js = Script;
        js.Should().Contain("function markerGlyph(name, variant)")
            .And.Contain("const cached = markerGlyphs.get(key);")
            .And.Contain("markerGlyphs.set(key, image);", "each glyph is drawn to a canvas once");
        foreach (var glyph in new[] { "chevron", "diamond", "dot", "ring-dot", "lens", "cone", "wedge", "snowflake", "plate", "viewer",
                     "outline", "heat", "triangle", "aqi", "hexagon", "rings", "crossed", "cross-circle", "diamond-outline", "shield", "bolt", "arcs" })
        {
            js.Should().Contain($"case '{glyph}':", $"the kit glyph '{glyph}' is drawn");
        }
        js.Should().Contain("const aircraftIcon = markerGlyph('chevron');")
            .And.Contain("const shipIcon = markerGlyph('diamond');")
            .And.Contain("image: markerGlyph('wedge'),")
            .And.Contain("case 'tg-travel':")
            .And.Contain("hatchLines(ring).forEach(", "weather alerts are hatched with plain lines, not a pattern material");
    }

    [Fact]
    public void NightOps_ShouldAddNoNewShaders()
    {
        var js = Script;
        js.Should().NotContain("postProcessStages.add", "WebKit can't compile post-process stages");
        System.Text.RegularExpressions.Regex.Matches(js, @"globe\.material = Cesium\.Material\.fromType\(").Should().HaveCount(2,
            "contours and slope stay the only globe materials, and they fail soft in onRenderError");
        js.Should().NotContain("new Cesium.ImageMaterialProperty")
            .And.NotContain("enableLighting = true");
    }

    [Fact]
    public void Selection_ShouldGetTargetBrackets_ThatGoWhenItsWindowCloses()
    {
        var js = Script;
        js.Should().Contain("function targetBracketImage()")
            .And.Contain("setTargetBrackets(containerId, target.position, target.size, target.extra, target.aircraftId);")
            .And.Contain("billboard: { image: targetBracketImage(), width: size, height: size, disableDepthTestDistance: Number.POSITIVE_INFINITY },");
        foreach (var close in new[] { "function closeAircraftPanel() {\n        aircraftRequestToken++;\n        clearTargetBrackets();",
                     "function closeIncidentPanel() {\n        clearTargetBrackets();", "function closeStreamPanel() {\n        clearTargetBrackets();" })
        {
            js.Replace("\r\n", "\n").Should().Contain(close);
        }
    }

    [Fact]
    public void ThermalTint_ShouldFollowTheStageAndQuality_AndRespectReducedMotion()
    {
        var js = Script;
        js.Should().Contain("return currentQuality() !== 'off' && currentStage !== 'orbit';")
            .And.Contain("onStageChanged(stage);")
            .And.Contain("if (on && !was && !reducedMotion())", "the warm-up fade is skipped under prefers-reduced-motion")
            .And.Contain("if (fire) addThermalHalo(dataSource, position, size);")
            .And.Contain("image: markerGlyph('engines')");
    }

    [Fact]
    public void LineOfSight_ShouldBeWebOnly_ThreeDAndHighQuality()
    {
        var js = Script;
        js.Should().Contain("return !!(entry && entry.threeDOn && currentQuality() === 'high');")
            .And.Contain("await Cesium.sampleTerrainMostDetailed(provider, cartographics)")
            .And.Contain("const LINE_OF_SIGHT = { cells: 40, radiusMetres: 3000,")
            .And.Contain("addLineOfSightAction(panel, camera);")
            .And.Contain("button.className = 'tg-los-clear';");
        var block = System.Text.RegularExpressions.Regex.Match(js, @"const nativeOperations = new Set\(\[(?<ops>[\s\S]*?)\]\);").Groups["ops"].Value;
        block.Should().NotContain("LineOfSight", "line of sight is web only (Night Ops plan, Mac section)");
    }

    [Fact]
    public void Quality_ShouldBeStoredSafely_AndShownOnThePage()
    {
        var js = Script;
        var page = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/OverwatchGrid.razor"));
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/OverwatchGrid.razor.css"));
        js.Should().Contain("try { window.localStorage.setItem(QUALITY_KEY, level); } catch (err)")
            .And.Contain("const stored = window.localStorage.getItem(QUALITY_KEY);")
            .And.Contain("battery.charging === false) applyQuality('standard', false);", "High is never the default on battery")
            .And.Contain("entry.threeD.buildings.show = entry.threeDOn && level === 'high';")
            .And.Contain("return 'failed: quality is Off';");
        page.Should().Contain("tacticalGlobe.setQuality").And.Contain("tacticalGlobe.getQuality").And.Contain("role=\"radiogroup\"");
        css.Should().Contain(".tg-shell[data-ow-quality=\"off\"] .tg-grain { display: none; }")
            .And.Contain(".tg-quality-options .tg-toggle { min-height: 44px; }");
    }
}
