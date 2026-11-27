#if MACCATALYST && DEBUG
using Foundation;
using WebKit;

namespace GwsBusinessSuite.App;

// The Overwatch visual tour on the real Apple map (26-11-26, Grant: every layer visually verified on
// the Mac app as well as the web/PWA). Debug builds only, and only when the app starts with
// GWS_OVERWATCH_SELFTEST=1. It opens Overwatch, waits for it to settle, and runs the shared tour
// (tests/.../TestData/overwatch-visual-tour.js, embedded in Debug builds). Pictures and the report go
// to ~/Downloads/gws-overwatch-selftest/<time>/ (MacNativeOverwatchBridge.SelfTest*). The tour puts
// back the layer switches it found on.
internal static class OverwatchSelfTest
{
    private static bool _started;

    public static bool Enabled => Environment.GetEnvironmentVariable("GWS_OVERWATCH_SELFTEST") == "1";

    public static void OnNavigated(WKWebView webView, string url)
    {
        if (!Enabled || _started || url.Contains("/login", StringComparison.OrdinalIgnoreCase)) return;
        if (!url.Contains("/admin/osint", StringComparison.OrdinalIgnoreCase) || url.Contains("/admin/osint/", StringComparison.OrdinalIgnoreCase))
        {
            webView.LoadRequest(new NSUrlRequest(new NSUrl(AppEndpoints.BaseUrl + "/admin/osint")));
            return;
        }
        _started = true;
        _ = RunAsync(webView);
    }

    private static async Task RunAsync(WKWebView webView)
    {
        // The page reloads once for the native map (MainPage), then loads its layers.
        await Task.Delay(TimeSpan.FromSeconds(25));
        using var stream = typeof(OverwatchSelfTest).Assembly.GetManifestResourceStream("overwatch-visual-tour.js");
        if (stream is null) { Console.WriteLine("[owtour] the tour script is missing from this build"); return; }
        using var reader = new StreamReader(stream);
        var tour = await reader.ReadToEndAsync();
        const string host = """
            (function () {
              let seq = 0; const pending = {};
              window.__owHostDone = function (id, result) { const done = pending[id]; delete pending[id]; if (done) done(result); };
              const call = function (operation, args) {
                return new Promise(function (resolve) {
                  const id = ++seq; pending[id] = resolve;
                  window.webkit.messageHandlers.gwsNativeOverwatch.postMessage({ operation: operation, arguments: [id].concat(args) });
                  setTimeout(function () { if (pending[id]) { delete pending[id]; resolve(false); } }, 20000);
                });
              };
              const host = {
                shot: function (name) { return call('selfTestShot', [name]); },
                select: function (key) { return call('selfTestSelect', [key]); },
                log: function (text) { console.log('[owtour] ' + text); call('selfTestLog', [text]); }
              };
              window.__owTour(host, __ONLY__).then(function (report) { return call('selfTestReport', [report.join('\n')]); })
                .catch(function (error) { return call('selfTestReport', ['TOUR FAILED: ' + String(error && error.stack || error)]); });
            })();
            """;
        // GWS_OVERWATCH_SELFTEST_ONLY=looks,aircraft runs only those steps.
        var only = Environment.GetEnvironmentVariable("GWS_OVERWATCH_SELFTEST_ONLY") is { Length: > 0 } list
            ? System.Text.Json.JsonSerializer.Serialize(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : "null";
        await MainThread.InvokeOnMainThreadAsync(async () => await webView.EvaluateJavaScriptAsync(tour + "\n" + host.Replace("__ONLY__", only, StringComparison.Ordinal)));
    }
}
#endif
