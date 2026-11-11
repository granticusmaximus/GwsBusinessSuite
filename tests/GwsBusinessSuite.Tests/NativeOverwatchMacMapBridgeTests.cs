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
}
