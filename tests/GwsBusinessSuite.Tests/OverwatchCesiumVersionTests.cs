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
        tacticalGlobe.Should().Contain("msaaSamples: 1",
            "the portable WebKit renderer must avoid multisample shader variants");
        tacticalGlobe.Should().Contain("requestWebgl1: window.__gwsMacCatalystWebView === true",
            "only the portable Mac Catalyst renderer must avoid WKWebView's WebGL 2 Metal shader path");
        tacticalGlobe.Should().Contain("reportNativeRendererFailure(reason)",
            "a native-only renderer failure must retain the actual WebKit/Cesium diagnostic");
        tacticalGlobe.Should().Contain("installNativeShaderDiagnostics()",
            "an empty WebKit shader compiler log must retain the failing shader source locally");
        tacticalGlobe.Should().Contain("viewer.scene.highDynamicRange = false",
            "the portable WebKit renderer must avoid HDR shader variants");
        tacticalGlobe.Should().Contain("viewer.scene.logarithmicDepthBuffer = false",
            "the portable WebKit renderer must avoid logarithmic-depth shader variants");
    }
}
