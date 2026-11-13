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
        bridge.Should().Contain("if (HitTest(point) is { } view)").And.Contain("Activate(view.Annotation);");
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

        bridge.Should().Contain("_map.AddAnnotations(added.ToArray())");
        bridge.Should().Contain("if (_signatures.TryGetValue(operation, out var last) && last == signature) return;");
        bridge.Should().Contain("if (previous.Remove(key, out var kept))");
        bridge.Should().Contain("_map.AddOverlays(overlays.ToArray())");
        bridge.Should().NotContain("_map.AddAnnotation(annotation)");
        bridge.Should().NotContain("_map.AddOverlay(overlay)");
        bridge.Should().Contain("view.DisplayPriority = MKFeatureDisplayPriority.DefaultLow");
    }

    [Fact]
    public void NativeMap_ShouldOwnGesturesInThePage_AndApplyThemWithoutReFittingTheRegion()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        var bridge = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacNativeOverwatchBridge.cs"));
        var bootstrap = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacWebViewCompatibility.cs"));

        // One place forwards gestures (the page script, behaviour covered by the MacApp_ browser
        // tests); the app's injected script only marks the page, with no document-wide observer.
        bootstrap.Should().Contain("window.__gwsMacCatalystWebView = true;");
        bootstrap.Should().NotContain("new MutationObserver").And.NotContain("addEventListener(");
        script.Should().Contain("const NATIVE_DRAG_THRESHOLD_PX = 5;");
        script.Should().Contain("requestAnimationFrame(flush)");
        script.Should().Contain("postNative('zoomBy', [factor, nativePendingZoom.x, nativePendingZoom.y])");
        script.Should().Contain("container.dataset.gwsNativeAppNavigation !== 'true'", "older app builds keep their own forwarding");

        bridge.Should().Contain("case \"pan\": Pan(arguments); return;");
        bridge.Should().Contain("case \"zoomBy\": ZoomBy(arguments); return;");
        bridge.Should().Contain("_map.SetCenterCoordinate(target, false)");
        bridge.Should().Contain("MKMapCamera.CameraLookingAtCenterCoordinate(center, distance, camera.Pitch, camera.Heading)");
        bridge.Should().Contain("Task.Delay(250, debounce.Token)");
        bridge.Should().Contain("owner.RegionDidChange(mapView.Region)");
    }

    [Fact]
    public void NativeMap_ShouldHitTestDrawnMarkers_ExpandClusters_AndReopenTheSamePin()
    {
        var bridge = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacNativeOverwatchBridge.cs"));

        bridge.Should().Contain("var frame = view.ConvertRectToView(view.Bounds, _map);")
            .And.Contain("if (frame.Contains(point))")
            .And.Contain("NativeOverwatchOperations.Selectable.Contains(pin.Operation)");
        bridge.Should().Contain("_map.ShowAnnotations(members, true)")
            .And.Contain("annotation is MKClusterAnnotation cluster")
            .And.Contain("ClusterCount(annotation).ToString(CultureInfo.InvariantCulture)");
        // MapKit can hand a cluster over as a plain IMKAnnotation wrapper; a bare type check missed
        // it, so clusters drew red ("7", "+6 more") and ignored clicks (12-11-26).
        bridge.Should().Contain("native.IsKindOfClass(ClusterClass)")
            .And.Contain("ValueForKey(new NSString(\"memberAnnotations\"))")
            .And.Contain("if (ClusterMembers(target) is { } members)")
            .And.Contain("if (IsCluster(annotation))")
            // The wrapper isn't an NSObject, only a native handle: the first fix checked `is NSObject`.
            .And.Contain("annotation is ObjCRuntime.INativeObject wrapper")
            .And.Contain("ObjCRuntime.Runtime.GetNSObject(wrapper.Handle)")
            .And.Contain("no pin within reach{why}")
            // A miss beside a drawn marker, and any failure, also reach the server's log through the
            // page (logging plan, 12-11-26); a click on open map stays local.
            .And.Contain("if (distance <= MissedPinReach) Report(line); else MapLog(line);")
            .And.Contain("window.gwsClientLog && window.gwsClientLog.report({json})")
            .And.Contain("origin = \"mac-app\", kind = \"native\"");
        bridge.Should().Contain("window.gwsNativeOverwatch && window.gwsNativeOverwatch.selected({json})")
            .And.Contain("mapView.DeselectAnnotation(annotation, false)")
            .And.Contain("view.CanShowCallout = false")
            .And.NotContain("_map.SelectAnnotation(");
        // A pile that zooming can't split (news at one city) goes to the page as a list, members
        // matched to our pins by native handle; every click outcome is logged (12-11-26).
        bridge.Should().Contain("var pins = members.Select(Resolve).OfType<NativeAnnotation>().Where(IsSelectable).ToList();")
            .And.Contain("native.selectedMany(group)")
            .And.Contain(".FirstOrDefault(pin => pin.Handle == handle)")
            .And.Contain("gws-overwatch-map.log")
            .And.Contain("Report($\"{operation} failed:")
            // Radar (12-11-26): the Mac app drew none. The latest frame, worldwide under the US layer,
            // stretched past each source's last zoom.
            .And.Contain("case \"setRadarTimeline\": SetRadarTimeline(arguments); return;")
            .And.Contain("new StretchedTileOverlay(_radarGlobal, 7)")
            .And.Contain("MKTileOverlay tiles => new MKTileOverlayRenderer(tiles)");
    }

    [Fact]
    public void NativeSelection_ShouldRouteExactlyTheSelectableLayers()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        var block = Regex.Match(script, @"const nativeSelectionHandlers = \{(?<body>[\s\S]*?)\n        \};");
        block.Success.Should().BeTrue();
        var routed = Regex.Matches(block.Groups["body"].Value, @"^\s{12}(?<operation>set[A-Z]\w*):", RegexOptions.Multiline)
            .Select(match => match.Groups["operation"].Value)
            .ToHashSet(StringComparer.Ordinal);

        routed.Should().BeEquivalentTo(NativeOverwatchOperations.Selectable);
        NativeOverwatchOperations.All.Should().Contain(NativeOverwatchOperations.Selectable);
    }
}
