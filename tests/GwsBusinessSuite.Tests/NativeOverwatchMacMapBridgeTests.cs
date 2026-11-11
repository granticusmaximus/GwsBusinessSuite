using System.Text.RegularExpressions;
using FluentAssertions;
using GwsBusinessSuite.Application.Overwatch;

namespace GwsBusinessSuite.Tests;

public sealed class NativeOverwatchMacMapBridgeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void MacAdapter_ShouldForwardEveryRendererContractOperation()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        var block = Regex.Match(script, @"const nativeOperations = new Set\(\[(?<operations>[\s\S]*?)\]\);");
        block.Success.Should().BeTrue();
        var forwarded = Regex.Matches(block.Groups["operations"].Value, @"'(?<operation>(?:set|clear)[A-Z]\w*)'")
            .Select(match => match.Groups["operation"].Value)
            .ToHashSet(StringComparer.Ordinal);

        forwarded.Should().BeSupersetOf(NativeOverwatchOperations.All,
            "the Mac renderer must not silently drop a layer supported by the web renderer contract");
        script.Should().Contain("postNative('mapTap'");
    }

    [Fact]
    public void MacApp_ShouldUseMapKitInsteadOfShaderInterception()
    {
        var bootstrap = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacWebViewCompatibility.cs"));
        var bridge = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacNativeOverwatchBridge.cs"));

        bootstrap.Should().Contain("MacNativeOverwatchBridge.Attach(webView)");
        bootstrap.Should().NotContain("shaderSource");
        bootstrap.Should().NotContain("gwsRendererDiagnostics");
        bridge.Should().Contain("MKMapView");
        bridge.Should().Contain("MKMarkerAnnotationView");
        bridge.Should().Contain("MKPolyline");
        bridge.Should().Contain("MKPolygon");
        bridge.Should().Contain("ClusteringIdentifier");
        bridge.Should().Contain("window.gwsNativeOverwatch");
        bridge.Should().Contain("case \"startPickPoint\"");
        bridge.Should().Contain("case \"mapTap\"");
        bridge.Should().Contain("_map.ConvertPoint(point, _map)");
        bridge.Should().Contain("_map.ConvertCoordinate(annotation.Coordinate, _map)");
        bridge.Should().Contain("SelectAnnotation");
        bridge.Should().Contain("case \"setThreeD\"");
        bridge.Should().Contain("MKMapType.HybridFlyover");
    }

    [Fact]
    public void MacAdapter_ShouldScaleTheViewportIntoNativeCoordinates_AndForwardZoomControls()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        var page = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/OverwatchGrid.razor"));
        var bridge = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacNativeOverwatchBridge.cs"));

        script.Should().Contain("rect.width, rect.height, window.innerWidth, window.innerHeight");
        page.Should().Contain("aria-label=\"Zoom in\"").And.Contain("aria-label=\"Zoom out\"");
        bridge.Should().Contain("webView.Bounds.Width / viewportWidth");
        bridge.Should().Contain("webView.Bounds.Height / viewportHeight");
        bridge.Should().Contain("webView.Frame.X + x * scaleX");
        bridge.Should().Contain("case \"zoom\": Zoom(arguments); return;");
        bridge.Should().Contain("_map.SetRegion(region, true)");
    }

    [Fact]
    public void MacAdapter_ShouldSelectNativeModeBeforeWebKitPublishesTheMessageHandler()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));

        script.Should().Contain("if (window.__gwsMacCatalystWebView === true) {");
        script.Should().NotContain("&& window.webkit.messageHandlers.gwsNativeOverwatch) {");
        script.Should().Contain("const waitForNativeHandler = function ()");
        script.Should().Contain("await waitForNativeHandler();");
        script.Should().Contain("The native Overwatch renderer did not become ready.");
    }

    [Fact]
    public void MacAdapter_ShouldMakeTheRootWebSurfacesTransparentAboveMapKit()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        var bridge = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacNativeOverwatchBridge.cs"));

        script.Should().Contain("document.documentElement.style.setProperty('background-color', 'transparent', 'important')");
        script.Should().Contain("document.body.style.setProperty('background-color', 'transparent', 'important')");
        bridge.Should().Contain("document.documentElement.style.setProperty('background-color','transparent','important')");
        bridge.Should().Contain("document.body.style.setProperty('background-color','transparent','important')");
        bridge.Should().Contain("webView.Opaque = false");
    }

    [Fact]
    public void NativeMap_ShouldBatchLargePinAndOverlayUpdates()
    {
        var bridge = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacNativeOverwatchBridge.cs"));

        bridge.Should().Contain("_map.AddAnnotations(annotations.ToArray())");
        bridge.Should().Contain("_map.AddOverlays(overlays.ToArray())");
        bridge.Should().NotContain("_map.AddAnnotation(annotation)");
        bridge.Should().NotContain("_map.AddOverlay(overlay)");
        bridge.Should().Contain("view.DisplayPriority = MKFeatureDisplayPriority.DefaultLow");
    }

    [Fact]
    public void NativeMap_ShouldForwardPanAndWheelGesturesThroughTheWebViewport()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        var bridge = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacNativeOverwatchBridge.cs"));

        script.Should().Contain("container.addEventListener('pointermove'");
        script.Should().Contain("postNative('pan', [deltaX, deltaY])");
        script.Should().Contain("container.addEventListener('wheel'");
        script.Should().Contain("event.preventDefault()");
        bridge.Should().Contain("case \"pan\": Pan(arguments); return;");
        bridge.Should().Contain("private void Pan(object?[] arguments)");
        bridge.Should().Contain("_map.SetRegion(region, false)");
        bridge.Should().Contain("Task.Delay(250, debounce.Token)");
        bridge.Should().Contain("owner.RegionDidChange(mapView.Region)");
    }
}
