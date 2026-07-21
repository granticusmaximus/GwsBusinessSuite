#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace GwsBusinessSuite.App;

internal static class TrustedWindowsDownloads
{
    public static void Configure(WebView2 webView)
    {
        webView.CoreWebView2Initialized += OnCoreWebView2Initialized;
        if (webView.CoreWebView2 is not null)
        {
            Configure(webView.CoreWebView2);
        }
    }

    private static void OnCoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (sender.CoreWebView2 is not null)
        {
            Configure(sender.CoreWebView2);
        }
    }

    private static void Configure(CoreWebView2 coreWebView)
    {
        coreWebView.DownloadStarting -= OnDownloadStarting;
        coreWebView.DownloadStarting += OnDownloadStarting;
    }

    private static void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.DownloadOperation.Uri, UriKind.Absolute, out var uri)
            || !AppEndpoints.IsTrusted(uri))
        {
            args.Cancel = true;
        }
    }
}
#endif
