using Foundation;
using WebKit;

namespace GwsBusinessSuite.App;

internal sealed class RendererDiagnosticsBridge : WKScriptMessageHandler
{
    public const string Name = "gwsRendererDiagnostics";
    private static int _externalFallbackOpened;

    private static readonly string LogPath =
        Path.Combine(FileSystem.Current.AppDataDirectory, "gws-renderer-diagnostics.log");

    public static void Attach(WKWebView webView)
    {
        var controller = webView.Configuration.UserContentController;
        controller.RemoveScriptMessageHandler(Name);
        controller.AddScriptMessageHandler(new RendererDiagnosticsBridge(), Name);
    }

    public override void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
    {
        if (message.Body is not NSString body) return;

        var diagnostic = body.ToString();
        if (string.IsNullOrWhiteSpace(diagnostic) || diagnostic.Length > 2_000) return;

        try
        {
            File.AppendAllText(LogPath, $"{DateTimeOffset.UtcNow:O} {diagnostic}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never disrupt the hosted workspace.
        }

        if (Interlocked.Exchange(ref _externalFallbackOpened, 1) != 0) return;

        MainThread.BeginInvokeOnMainThread(() =>
            _ = Launcher.Default.OpenAsync(new Uri($"{AppEndpoints.BaseUrl}/admin/osint")));
    }
}
