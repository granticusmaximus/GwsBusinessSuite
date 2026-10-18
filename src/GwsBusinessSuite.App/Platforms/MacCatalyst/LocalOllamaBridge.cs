using System.Collections.Concurrent;
using System.Net;
using Foundation;
using WebKit;

namespace GwsBusinessSuite.App;

// Lets Content Studio (served from https://admin.gwsapp.net) use the Ollama on this Mac. The page
// can't fetch http://localhost itself: WebKit blocks a secure page loading plain-http content
// ("mixed content"), whatever OLLAMA_ORIGINS says. So wwwroot/js/local-ollama.js posts the request
// to this handler (window.webkit.messageHandlers.gwsOllama), the app makes the call natively, and
// streams the response bytes back to window.gwsOllamaNative.
//
// Kept narrow on purpose: only the trusted site's main frame, only GET /api/tags and
// POST /api/generate, and only to Ollama on this machine - never a general-purpose proxy.
internal sealed class LocalOllamaBridge(WKWebView webView) : WKScriptMessageHandler
{
    public const string Name = "gwsOllama";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false })
    {
        // Generation can take many minutes; the page's own abort (and probe timeout) end it.
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly WeakReference<WKWebView> _webView = new(webView);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public static void Attach(WKWebView webView)
    {
        var controller = webView.Configuration.UserContentController;
        // Mappings can run more than once for the same view; WebKit throws on a duplicate name.
        controller.RemoveScriptMessageHandler(Name);
        controller.AddScriptMessageHandler(new LocalOllamaBridge(webView), Name);
    }

    public override void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
    {
        if (!IsTrustedSender(message) || message.Body is not NSDictionary body) return;
        var id = (body["id"] as NSString)?.ToString();
        var op = (body["op"] as NSString)?.ToString();
        if (string.IsNullOrEmpty(id) || id.Length > 64) return;

        if (op == "abort")
        {
            if (_running.TryRemove(id, out var running)) running.Cancel();
            return;
        }
        if (op != "fetch") return;

        var path = (body["path"] as NSString)?.ToString() ?? string.Empty;
        var method = (body["method"] as NSString)?.ToString() ?? "GET";
        var port = (body["port"] as NSNumber)?.Int32Value ?? 11434;
        var content = (body["body"] as NSString)?.ToString();
        if (!((path == "/api/tags" && method == "GET") || (path == "/api/generate" && method == "POST")) || port is < 1 or > 65535)
        {
            _ = SendAsync($"window.gwsOllamaNative && window.gwsOllamaNative.fail({Js(id)}, {Js("That request isn't allowed.")})");
            return;
        }

        var cts = new CancellationTokenSource();
        _running[id] = cts;
        _ = RunAsync(id, new Uri($"http://127.0.0.1:{port}{path}"), method, content, cts);
    }

    private async Task RunAsync(string id, Uri url, string method, string? content, CancellationTokenSource cts)
    {
        try
        {
            using var request = new HttpRequestMessage(method == "POST" ? HttpMethod.Post : HttpMethod.Get, url);
            if (content is not null) request.Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await SendAsync($"window.gwsOllamaNative && window.gwsOllamaNative.head({Js(id)}, {(int)response.StatusCode})");

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                // Base64 keeps arbitrary bytes (split UTF-8 included) safe inside a JS string.
                await SendAsync($"window.gwsOllamaNative && window.gwsOllamaNative.chunk({Js(id)}, '{Convert.ToBase64String(buffer, 0, read)}')");
            }
            await SendAsync($"window.gwsOllamaNative && window.gwsOllamaNative.end({Js(id)})");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The page aborted it and already settled its side.
        }
        catch (Exception ex)
        {
            var reason = ex is HttpRequestException { InnerException: System.Net.Sockets.SocketException }
                || ex is HttpRequestException { StatusCode: null }
                ? $"Couldn't reach Ollama on this Mac ({url.Host}:{url.Port}). Make sure Ollama is running."
                : $"Local Ollama: {ex.Message}";
            await SendAsync($"window.gwsOllamaNative && window.gwsOllamaNative.fail({Js(id)}, {Js(reason)})");
        }
        finally
        {
            _running.TryRemove(id, out _);
            cts.Dispose();
        }
    }

    // Only the signed-in site's own page, in the main frame (no embedded third-party frame).
    private static bool IsTrustedSender(WKScriptMessage message)
    {
        var frame = message.FrameInfo;
        if (frame is null || !frame.MainFrame) return false;
        var origin = frame.SecurityOrigin;
        var port = origin.Port > 0 ? $":{origin.Port}" : string.Empty;
        return Uri.TryCreate($"{origin.Protocol}://{origin.Host}{port}", UriKind.Absolute, out var uri) && AppEndpoints.IsTrusted(uri);
    }

    private Task SendAsync(string script) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (_webView.TryGetTarget(out var view)) await view.EvaluateJavaScriptAsync(script);
        });

    // A JS string literal.
    private static string Js(string value) =>
        "'" + value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n").Replace("<", "\\x3c") + "'";
}
