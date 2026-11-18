using System.Runtime.CompilerServices;
using Foundation;
using WebKit;

namespace GwsBusinessSuite.App;

internal static class MacWebViewCompatibility
{
    private static readonly ConditionalWeakTable<WKWebView, object> ConfiguredViews = new();
    // Only marks the page as running in the Mac app. tactical-globe.js then hands the map to
    // MacNativeOverwatchBridge and owns the pointer, wheel and click forwarding itself. (Until
    // 12-11-26 this script also forwarded gestures, through a MutationObserver that ran on every
    // DOM change of every page, posting each pointer move and wheel tick separately.)
    // __gwsMacNativeChrome (18-11-26): this build's toolbar has Sidebar and Search, so the page's
    // top bar drops its own copies (theme-init.js and app.js). An older app without it keeps them.
    internal const string Script = """
        window.__gwsMacCatalystWebView = true;
        window.__gwsMacNativeChrome = true;
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
