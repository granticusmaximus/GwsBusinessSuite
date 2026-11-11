using System.Runtime.CompilerServices;
using Foundation;
using WebKit;

namespace GwsBusinessSuite.App;

internal static class MacWebViewCompatibility
{
    private static readonly ConditionalWeakTable<WKWebView, object> ConfiguredViews = new();
    internal const string Script = """
        window.__gwsMacCatalystWebView = true;
        """;

    public static void Configure(WKWebView webView)
    {
        if (ConfiguredViews.TryGetValue(webView, out _)) return;
        ConfiguredViews.Add(webView, new object());

        MacNativeOverwatchBridge.Attach(webView);
        webView.Configuration.UserContentController.AddUserScript(
            new WKUserScript(
                new NSString(Script),
                WKUserScriptInjectionTime.AtDocumentStart,
                false));
    }
}
