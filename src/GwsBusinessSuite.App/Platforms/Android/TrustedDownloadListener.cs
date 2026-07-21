#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Android.Webkit;
using Microsoft.Maui.ApplicationModel;
using AEnvironment = Android.OS.Environment;

namespace GwsBusinessSuite.App;

internal sealed class TrustedDownloadListener : Java.Lang.Object, IDownloadListener
{
    public void OnDownloadStart(
        string? url,
        string? userAgent,
        string? contentDisposition,
        string? mimetype,
        long contentLength)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AppEndpoints.IsTrusted(uri))
        {
            return;
        }

        _ = EnqueueAsync(uri, userAgent, contentDisposition, mimetype);
    }

    private static async Task EnqueueAsync(
        Uri uri,
        string? userAgent,
        string? contentDisposition,
        string? mimeType)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)
            && await Permissions.RequestAsync<Permissions.StorageWrite>() != PermissionStatus.Granted)
        {
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var context = Android.App.Application.Context;
            var manager = context.GetSystemService(Context.DownloadService) as DownloadManager;
            if (manager is null)
            {
                return;
            }

            var suggestedName = URLUtil.GuessFileName(uri.AbsoluteUri, contentDisposition, mimeType);
            var fileName = Path.GetFileName(suggestedName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"gws-download-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            }

            var request = new DownloadManager.Request(Android.Net.Uri.Parse(uri.AbsoluteUri));
            request.SetTitle(fileName);
            request.SetDescription("Downloaded from GWS Business Suite");
            request.SetNotificationVisibility(DownloadVisibility.VisibleNotifyCompleted);
            request.SetDestinationInExternalPublicDir(AEnvironment.DirectoryDownloads, fileName);

            if (!string.IsNullOrWhiteSpace(mimeType))
            {
                request.SetMimeType(mimeType);
            }

            if (!string.IsNullOrWhiteSpace(userAgent))
            {
                request.AddRequestHeader("User-Agent", userAgent);
            }

            var cookies = CookieManager.Instance?.GetCookie(uri.AbsoluteUri);
            if (!string.IsNullOrWhiteSpace(cookies))
            {
                request.AddRequestHeader("Cookie", cookies);
            }

            manager.Enqueue(request);
        });
    }
}
#endif
