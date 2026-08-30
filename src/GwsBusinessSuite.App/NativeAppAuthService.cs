using System.Net;

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
}
