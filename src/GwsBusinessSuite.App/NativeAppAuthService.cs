using System.Net;
using System.Net.Http.Json;

namespace GwsBusinessSuite.App;

public sealed record DeviceLoginResult(bool Succeeded, string? ErrorMessage, IReadOnlyList<string> SetCookieHeaders);

// Calls the server's /auth/device-login (see IUserManagementService.AttemptDeviceLoginAsync) -
// the native app's alternative to the browser-facing MFA challenge. The injected HttpClient must
// be configured with UseCookies = false (see MauiProgram.cs) so the raw Set-Cookie header
// survives on the response instead of being consumed into an HttpClientHandler-internal
// CookieContainer this app never reads from.
public sealed class NativeAppAuthService(HttpClient httpClient)
{
    public async Task<DeviceLoginResult> LoginAsync(
        string deviceSecret, string username, string password, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["deviceSecret"] = deviceSecret,
            ["username"] = username,
            ["password"] = password
        });

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsync(
                new Uri(new Uri(AppEndpoints.BaseUrl), "/auth/device-login"), content, cancellationToken);
        }
        catch (Exception ex)
        {
            return new DeviceLoginResult(false, $"Could not reach the server: {ex.Message}", []);
        }

        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode == HttpStatusCode.Locked
                ? "This account is temporarily locked out after too many failed attempts."
                : "Invalid device secret, username, or password.";
            return new DeviceLoginResult(false, message, []);
        }

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        return new DeviceLoginResult(true, null, cookies);
    }

    // The address that signs the web view in with Touch ID (opened IN the web view, so the session
    // cookie lands in its own store): the server's one-time challenge, signed after Touch ID.
    public async Task<(string? Url, string? Error)> PrepareTouchIdWebSignInAsync(TouchIdKeyStore store, CancellationToken cancellationToken = default)
    {
        if (store.Info is not { ServerKeyId: var keyId } || keyId == Guid.Empty) return (null, "Touch ID isn't set up on this Mac.");
        try
        {
            var baseUri = new Uri(AppEndpoints.BaseUrl);
            using var challengeResponse = await httpClient.PostAsJsonAsync(new Uri(baseUri, "/auth/touchid/challenge"), new { keyId }, cancellationToken);
            if (!challengeResponse.IsSuccessStatusCode) return (null, "Sentinel didn't answer.");
            using var challengeJson = System.Text.Json.JsonDocument.Parse(await challengeResponse.Content.ReadAsStringAsync(cancellationToken));
            var challenge = Convert.FromBase64String(challengeJson.RootElement.GetProperty("challenge").GetString() ?? string.Empty);
            var (signature, error) = await store.SignAsync(challenge, $"Sign in to GWS Business Suite{(store.Info.Username is { } name ? $" as {name}" : string.Empty)}");
            if (signature is null) return (null, error);
            return (new Uri(baseUri, $"/auth/touchid/web-login?keyId={keyId}&signature={Uri.EscapeDataString(Convert.ToBase64String(signature))}").AbsoluteUri, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        {
            return (null, $"Could not reach the server: {ex.Message}");
        }
    }
}
