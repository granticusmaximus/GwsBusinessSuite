using FluentAssertions;

namespace GwsBusinessSuite.Tests;

public sealed class NativeOverwatchWebGlCompatibilityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void MacWebView_ShouldRemoveTheConfirmedFailingFloatDefinesBeforeShaderCompilation()
    {
        var compatibility = File.ReadAllText(Path.Combine(
            RepoRoot, "src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacWebViewCompatibility.cs"));
        var mainPage = File.ReadAllText(Path.Combine(
            RepoRoot, "src/GwsBusinessSuite.App/MainPage.xaml.cs"));

        compatibility.Should().Contain("WKUserScriptInjectionTime.AtDocumentStart",
            "the compatibility shim must exist before Cesium captures WebGL methods");
        compatibility.Should().Contain("contextType.prototype.shaderSource");
        compatibility.Should().Contain("#define OES_texture_float(?:_linear)?");
        compatibility.Should().Contain("sampler3D\\s*;",
            "the confirmed failing shader also contains an unused sampler3D precision declaration");
        compatibility.Should().Contain("#line\\s+0",
            "WebKit's Metal translator rejects Cesium's zero-valued GLSL line directives");
        compatibility.Should().Contain("originalShaderSource.call(this, shader, compatibleSource)");
        mainPage.Should().Contain("WorkspaceView.HandlerChanged += OnWorkspaceViewHandlerChanged",
            "the compatibility setup must be attached to the WKWebView that actually hosts the workspace");
        mainPage.Should().Contain("OnWorkspaceViewHandlerChanged(WorkspaceView, EventArgs.Empty)",
            "the handler can already exist before the constructor subscribes to HandlerChanged");
        mainPage.Should().MatchRegex("OnAppearing\\(\\)[\\s\\S]*OnWorkspaceViewHandlerChanged\\(WorkspaceView, EventArgs.Empty\\)",
            "the actual workspace WKWebView must be configured before StartWorkspaceAsync assigns its first URL");
        mainPage.Should().Contain("await WorkspaceView.EvaluateJavaScriptAsync(MacWebViewCompatibility.Script)",
            "successful navigation is the first lifecycle point guaranteed to expose the actual workspace WKWebView");
        mainPage.Should().Contain("webView.ReloadFromOrigin()",
            "an Overwatch document that initialized before the late compatibility install must get one clean retry");
    }
}
