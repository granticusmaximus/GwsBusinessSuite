#if MACCATALYST
using Foundation;
using WebKit;
#endif

namespace GwsBusinessSuite.App;

// Puts the session cookie NativeAppAuthService captured from /auth/device-login's response into
// the WKWebView's own cookie store, so WorkspaceView arrives at AppEndpoints.StartUrl already
// signed in - Login.razor is never rendered. MacCatalyst only; a no-op everywhere else.
public static class NativeCookieInjector
{
    public static async Task InjectCookiesAsync(IReadOnlyList<string> setCookieHeaders, string baseUrl)
    {
#if MACCATALYST
        if (setCookieHeaders.Count == 0) return;
        var url = NSUrl.FromString(baseUrl);
        if (url is null) return;

        var store = WKWebsiteDataStore.DefaultDataStore.HttpCookieStore;

        // Processed one header at a time (not joined with ", ") - NSHttpCookie's own
        // CookiesWithResponseHeaderFields parser is the correct way to turn a raw Set-Cookie
        // string into an NSHttpCookie, and joining multiple Set-Cookie headers together is an
        // established source of ambiguity (a cookie's own Expires attribute contains a comma).
        // In practice this endpoint only ever sets the one auth cookie.
        foreach (var header in setCookieHeaders)
        {
            using var headerDict = new NSMutableDictionary();
            headerDict[new NSString("Set-Cookie")] = new NSString(header);
            var cookies = NSHttpCookie.CookiesWithResponseHeaderFields(headerDict, url);
            foreach (var cookie in cookies)
            {
                var tcs = new TaskCompletionSource();
                store.SetCookie(cookie, () => tcs.SetResult());
                await tcs.Task;
            }
        }
#else
        await Task.CompletedTask;
#endif
    }
}
