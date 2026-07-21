#if IOS || MACCATALYST
using Foundation;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Storage;
using WebKit;

namespace GwsBusinessSuite.App;

internal sealed class TrustedDownloadNavigationDelegate(IWebViewHandler handler)
    : MauiWebViewNavigationDelegate(handler)
{
    // Apple's download policy has the native value 2. The current .NET Apple bindings expose
    // WKDownload but omit the Download enum members, so keep the platform value in one place.
    private const WKNavigationActionPolicy ActionDownloadPolicy = (WKNavigationActionPolicy)2;
    private const WKNavigationResponsePolicy ResponseDownloadPolicy = (WKNavigationResponsePolicy)2;
    private readonly TrustedAppleDownloadDelegate _downloadDelegate = new();

    public override void DecidePolicy(
        WKWebView webView,
        WKNavigationAction navigationAction,
        Action<WKNavigationActionPolicy> decisionHandler)
    {
        if (navigationAction.ShouldPerformDownload)
        {
            decisionHandler(IsTrusted(navigationAction.Request.Url)
                ? ActionDownloadPolicy
                : WKNavigationActionPolicy.Cancel);
            return;
        }

        base.DecidePolicy(webView, navigationAction, decisionHandler);
    }

    [Export("webView:decidePolicyForNavigationResponse:decisionHandler:")]
    public void DecidePolicy(
        WKWebView webView,
        WKNavigationResponse navigationResponse,
        Action<WKNavigationResponsePolicy> decisionHandler)
    {
        if (!navigationResponse.CanShowMimeType)
        {
            decisionHandler(IsTrusted(navigationResponse.Response.Url)
                ? ResponseDownloadPolicy
                : WKNavigationResponsePolicy.Cancel);
            return;
        }

        decisionHandler(WKNavigationResponsePolicy.Allow);
    }

    [Export("webView:navigationAction:didBecomeDownload:")]
    public void NavigationActionDidBecomeDownload(
        WKWebView webView,
        WKNavigationAction navigationAction,
        WKDownload download) => download.WeakDelegate = _downloadDelegate;

    [Export("webView:navigationResponse:didBecomeDownload:")]
    public void NavigationResponseDidBecomeDownload(
        WKWebView webView,
        WKNavigationResponse navigationResponse,
        WKDownload download) => download.WeakDelegate = _downloadDelegate;

    private static bool IsTrusted(NSUrl? url) =>
        Uri.TryCreate(url?.AbsoluteString, UriKind.Absolute, out var uri) && AppEndpoints.IsTrusted(uri);
}

internal sealed class TrustedAppleDownloadDelegate : WKDownloadDelegate
{
    private readonly Dictionary<WKDownload, string> _destinationPaths = [];

    public override void DecideDestination(
        WKDownload download,
        NSUrlResponse response,
        string suggestedFilename,
        Action<NSUrl> completionHandler)
    {
        var fileName = SanitizeFileName(suggestedFilename);
        var destinationPath = CreateUniquePath(FileSystem.Current.CacheDirectory, fileName);
        _destinationPaths[download] = destinationPath;
        completionHandler(NSUrl.FromFilename(destinationPath));
    }

    public override void DidFinish(WKDownload download)
    {
        if (!_destinationPaths.Remove(download, out var completedPath) || !File.Exists(completedPath))
        {
            return;
        }

        _ = MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(
            new ShareFileRequest("Save or share GWS download", new ShareFile(completedPath))));
    }

    public override void DidFail(WKDownload download, NSError error, NSData? resumeData)
    {
        if (_destinationPaths.Remove(download, out var destinationPath) && File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }
    }

    private static string SanitizeFileName(string suggestedFilename)
    {
        var fileName = Path.GetFileName(suggestedFilename);
        return string.IsNullOrWhiteSpace(fileName)
            ? $"gws-download-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : fileName;
    }

    private static string CreateUniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }
}
#endif
