using System.Text.RegularExpressions;
using FluentAssertions;
using GwsBusinessSuite.Application.Overwatch;

namespace GwsBusinessSuite.Tests;

// Night Ops x Apple, combined (20-11-26): the kit's marker shapes on the web globe, target brackets,
// thermal tint, line of sight and the quality setting. Source guards; the browser checks are in
// OverwatchNightOpsBrowserTests.
public sealed class OverwatchNightOpsMarkerTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    private static string Script => File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));

    // The Mac look (26-11-26, Grant: the Mac app is the reference): web pins are the Mac's discs. Every
    // SF Symbol the Mac can draw (NativeShapes.Marker) has its Bootstrap Icons twin on the web, and the
    // web's tones are NativeShapes.ToneHex. The drawn-pin check is in OverwatchNightOpsBrowserTests.
    [Fact]
    public void WebPins_ShouldBeTheMacDiscs_WithAnIconForEverySymbolTheMacUses()
    {
        var js = Script;
        var shapes = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Application/Overwatch/NativeShapes.cs"));
        var marker = shapes[shapes.IndexOf("public static MarkerStyle Marker(", StringComparison.Ordinal)..shapes.IndexOf("private static string? Text(", StringComparison.Ordinal)];
        var macSymbols = Regex.Matches(marker, "new\\(\"(?<s>[a-z0-9.]+)\"").Select(m => m.Groups["s"].Value).ToHashSet();
        macSymbols.Should().Contain(["airplane", "camera.fill", "newspaper.fill", "mappin"]);
        var table = Regex.Match(js, @"const SF_TO_BI = \{(?<t>[\s\S]*?)\};").Groups["t"].Value;
        var webSymbols = Regex.Matches(table, "'(?<s>[a-z0-9.]+)': '").Select(m => m.Groups["s"].Value).ToHashSet();
        macSymbols.Except(webSymbols).Should().BeEmpty("every symbol the Mac map draws has an icon on the web");

        foreach (var tone in Enum.GetValues<NativeShapes.MarkerTone>())
        {
            var hex = NativeShapes.ToneHex(tone);
            if (hex == "#59f07a") js.Should().NotContain($"{tone}: '#", $"{tone} falls back to the Night Ops green, as on the Mac");
            else js.Should().Contain($"{tone}: '{hex}'", $"the web's {tone} is the Mac's");
        }
        js.Should().Contain("styleMacCluster(clusteredEntities, cluster);", "groups are the Mac's count discs")
            .And.Contain("const MAC_CALLOUT_FONT = 'bold 10px Menlo", "callouts use the Mac's Menlo Bold box")
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
