using System.Runtime.CompilerServices;
using Foundation;
using WebKit;

namespace GwsBusinessSuite.App;

internal static class MacWebViewCompatibility
{
    private static readonly ConditionalWeakTable<WKWebView, object> ConfiguredViews = new();
    internal const string Script = """
        window.__gwsMacCatalystWebView = true;
        window.__gwsNativeRendererDiagnostic = function (message) {
            try {
                window.webkit.messageHandlers.gwsRendererDiagnostics.postMessage(String(message).slice(0, 2000));
            } catch (_) {}
        };
        window.__gwsNativeRendererDiagnostic('Native WKWebView compatibility script active.');
        (function installMacCatalystShaderCompatibility() {
            if (window.__gwsMacCatalystShaderCompatibilityInstalled) return;
            window.__gwsMacCatalystShaderCompatibilityInstalled = true;
            [window.WebGLRenderingContext, window.WebGL2RenderingContext].forEach(function (contextType) {
                if (!contextType) return;
                const originalShaderSource = contextType.prototype.shaderSource;
                contextType.prototype.shaderSource = function (shader, source) {
                    let compatibleSource = source;
                    if (typeof source === 'string' && this.getShaderParameter(shader, this.SHADER_TYPE) === this.FRAGMENT_SHADER) {
                        // WKWebView's Metal translator rejects Cesium's generated
                        // float feature declarations even in a texture-copy shader
                        // that does not use a 3D sampler. Remove only those confirmed
                        // declarations in this native content world.
                        compatibleSource = source
                            .replace(/^\s*#define OES_texture_float(?:_linear)?\s*$/gm, '')
                            .replace(/^\s*precision\s+(?:lowp|mediump|highp)\s+sampler3D\s*;\s*$/gm, '')
                            .replace(/^\s*#line\s+0\s*$/gm, '');
                        if (compatibleSource !== source && !window.__gwsMacCatalystFloatShaderSanitized) {
                            window.__gwsMacCatalystFloatShaderSanitized = true;
                            window.__gwsNativeRendererDiagnostic('Removed Cesium float-texture declarations and zero-line directives before native shader compilation.');
                        }
                    }
                    return originalShaderSource.call(this, shader, compatibleSource);
                };
            });
        })();
        """;

    public static void Configure(WKWebView webView)
    {
        if (ConfiguredViews.TryGetValue(webView, out _)) return;
        ConfiguredViews.Add(webView, new object());

        RendererDiagnosticsBridge.Attach(webView);
        webView.Configuration.UserContentController.AddUserScript(
            new WKUserScript(
                new NSString(Script),
                WKUserScriptInjectionTime.AtDocumentStart,
                false));
    }
}
