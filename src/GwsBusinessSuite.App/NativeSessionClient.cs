using System.Net.Http.Json;
using System.Text.Json;
#if MACCATALYST
using WebKit;
#endif

namespace GwsBusinessSuite.App;

public sealed record QuickNoteSaveResult(string? Url, string? Error);

// Native-side calls to the hosted app that need the signed-in session rather than a read-only
// Developer API key - today, saving a Quick Note (voice notes, screen captures). The session is
// whatever the Workspace tab's WebView is signed in with (device login or the normal sign-in),
// read from its cookie store, so there's no second sign-in and nothing new stored.
public sealed record ContactCreateResult(string? Url, string? FullName, string? Error);

public sealed class NativeSessionClient(HttpClient httpClient)
{
    public async Task<QuickNoteSaveResult> SaveQuickNoteAsync(string title, string markdown, CancellationToken cancellationToken = default)
    {
        var (json, error) = await PostAsync("/admin/api/native/quick-notes", new { title, markdown }, cancellationToken);
        return json is { } body ? new(AppEndpoints.BaseUrl + body.GetProperty("url").GetString(), null) : new(null, error);
    }

    public async Task<ContactCreateResult> CreateContactAsync(string fullName, string email, string company, CancellationToken cancellationToken = default)
    {
        var (json, error) = await PostAsync("/admin/api/native/contacts", new { fullName, email, company }, cancellationToken);
        return json is { } body
            ? new(AppEndpoints.BaseUrl + body.GetProperty("url").GetString(), body.GetProperty("fullName").GetString(), null)
            : new(null, null, error);
    }

    // POSTs JSON with the WebView's session; the parsed JSON on success, or a message to show.
    private async Task<(JsonElement? Json, string? Error)> PostAsync(string path, object payload, CancellationToken cancellationToken)
    {
        var cookieHeader = await GetSessionCookieHeaderAsync();
        if (string.IsNullOrEmpty(cookieHeader))
            return (null, "Sign in on the Workspace tab first - this uses that session.");

        using var request = new HttpRequestMessage(HttpMethod.Post, AppEndpoints.BaseUrl + path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("Cookie", cookieHeader);
        request.Headers.Add("X-GWS-Native", "1");
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // An expired session redirects to the sign-in page, which answers 200 with HTML.
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                return (null, "Your session has expired - sign in again on the Workspace tab, then retry.");
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.Clone();
            if (!response.IsSuccessStatusCode)
            {
                return (null, root.TryGetProperty("error", out var error) ? error.GetString() : $"The server said no (HTTP {(int)response.StatusCode}).");
            }
            return (root, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, "Couldn't reach the server.");
        }
    }

    private static async Task<string?> GetSessionCookieHeaderAsync()
    {
#if MACCATALYST
        if (!Uri.TryCreate(AppEndpoints.BaseUrl, UriKind.Absolute, out var baseUri)) return null;
        var cookies = await WKWebsiteDataStore.DefaultDataStore.HttpCookieStore.GetAllCookiesAsync();
        var mine = cookies
            .Where(c => baseUri.Host.EndsWith(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Name}={c.Value}")
            .ToList();
        return mine.Count == 0 ? null : string.Join("; ", mine);
#else
        await Task.CompletedTask;
        return null;
#endif
    }
}
