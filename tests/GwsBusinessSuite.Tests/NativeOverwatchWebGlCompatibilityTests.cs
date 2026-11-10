using FluentAssertions;

namespace GwsBusinessSuite.Tests;

public sealed class NativeOverwatchWebGlCompatibilityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void MacWebView_ShouldRemoveTheConfirmedFailingFloatDefinesBeforeShaderCompilation()
    {
        var mauiProgram = File.ReadAllText(Path.Combine(
            RepoRoot, "src/GwsBusinessSuite.App/MauiProgram.cs"));

        mauiProgram.Should().Contain("WKUserScriptInjectionTime.AtDocumentStart",
            "the compatibility shim must exist before Cesium captures WebGL methods");
        mauiProgram.Should().Contain("contextType.prototype.shaderSource");
        mauiProgram.Should().Contain("#define OES_texture_float(?:_linear)?");
        mauiProgram.Should().Contain("originalShaderSource.call(this, shader, compatibleSource)");
    }
}
