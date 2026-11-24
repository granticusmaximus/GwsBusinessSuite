using FluentAssertions;

namespace GwsBusinessSuite.Tests;

public sealed class OverwatchCesiumVersionTests
{
    private const string SafeVersion = "cesium@1.144.0";
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void CesiumUrls_ShouldStayOnTheSamePreRegressionRelease()
    {
        var files = new[]
        {
            "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/OverwatchGrid.razor",
            "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js",
            "src/GwsBusinessSuite.Web/wwwroot/js/overwatch-prefetch.js",
            "tests/GwsBusinessSuite.Tests/OverwatchGridScriptBrowserTests.cs"
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot, file));
            source.Should().Contain(SafeVersion,
                $"{file} must use the release before CesiumGS/cesium#13909 introduced the globe shader regression");
            source.Should().NotContain("cesium@1.145" + ".0",
                $"{file} must not restore the Cesium release with the globe shader regression");
        }

        var tacticalGlobe = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        tacticalGlobe.Should().Contain("cesiumVersionCacheBuster = 'v=1.144.0'",
            "the Mac WKWebView must bypass stale Cesium bundles with a versioned cache-buster");
        tacticalGlobe.Should().Contain("showRenderLoopErrors: false",
            "Cesium's blocking render-error dialog must not replace the contained globe fallback");
        tacticalGlobe.Should().Contain("viewer.scene.renderError.addEventListener(onRenderError)",
            "shader variants that fail after Viewer construction must use the same fallback");
        tacticalGlobe.Should().Contain("msaaSamples: isMacCatalyst ? 1 : 4",
            "the portable WebKit renderer must avoid multisample shader variants");
        tacticalGlobe.Should().Contain("requestWebgl1: false",
            "the native renderer must avoid WKWebView's defective WebGL 1 shader compiler");
        tacticalGlobe.Should().Contain("reportNativeRendererFailure(reason)",
            "a native-only renderer failure must retain the actual WebKit/Cesium diagnostic");
        tacticalGlobe.Should().Contain("installNativeShaderDiagnostics()",
            "an empty WebKit shader compiler log must retain the failing shader source locally");
        tacticalGlobe.Should().Contain("viewer.scene.sun.show = false",
            "the native renderer must avoid WebKit's incompatible cosmetic SunTextureFS shader");
        tacticalGlobe.Should().Contain("viewer.scene.moon.show = false",
            "the native renderer must avoid WebKit's incompatible cosmetic EllipsoidFS moon shader");
        tacticalGlobe.Should().Contain("viewer.scene.postProcessStages.enabled = false",
            "the native renderer must avoid WebKit's incompatible pass-through compositor shader");
        tacticalGlobe.Should().Contain("installNativeWebGlCompatibility()",
            "the native renderer must suppress WebKit's falsely advertised float-texture extensions");
        tacticalGlobe.Should().Contain("orderIndependentTranslucency: false,",
            "the native renderer must avoid WebKit's incompatible MRT transparency compositor");
        tacticalGlobe.Should().Contain("HTMLCanvasElement.prototype.getContext",
            "the native renderer must block WebKit's falsely advertised float-texture extensions on each Cesium context");
        tacticalGlobe.Should().Contain("viewer.scene.highDynamicRange = false",
            "the portable WebKit renderer must avoid HDR shader variants");
        tacticalGlobe.Should().Contain("viewer.scene.logarithmicDepthBuffer = false",
            "the portable WebKit renderer must avoid logarithmic-depth shader variants");
    }

    // Night Ops phase 3 (20-11-26): terrain shading is the one optional globe shader. A browser that
    // can't compile it must lose only the shading, never the globe (Safari and the Mac app's WebKit).
    [Fact]
    public void TerrainShading_ShouldFailSoftBeforeTheGlobeFallback()
    {
        var js = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        var handler = js.IndexOf("const onRenderError = function (scene, error) {", StringComparison.Ordinal);
        var soft = js.IndexOf("scene.globe.material = undefined;", handler, StringComparison.Ordinal);
        var hard = js.IndexOf("handleShaderFailure(containerId, error)", handler, StringComparison.Ordinal);
        Assert.True(handler > 0 && soft > handler && hard > soft, "the shading is removed before the globe's own shader fallback runs");
        Assert.Contains("Cesium.Material.fromType('ElevationContour'", js);
        Assert.Contains("Cesium.Material.fromType('SlopeRamp'", js);
        Assert.Contains("return 'failed: turn on 3D terrain first';", js);
        Assert.Contains("setTerrainShading: setTerrainShading,", js);
        Assert.Contains("viewer.scene.postProcessStages.enabled = false", js);
    }
}
