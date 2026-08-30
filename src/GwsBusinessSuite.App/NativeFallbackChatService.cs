using System.Net;
using System.Net.Http.Json;

namespace GwsBusinessSuite.App;

public sealed record FallbackChatResult(bool Succeeded, string? Completion, string? ErrorMessage);

// Calls the server's /native/fallback-chat - SentinelGptPage's reliability fallback for when the
// user's own local Ollama can't handle a turn (not installed, unreachable, or timed out). See
// that endpoint's comment in Program.cs for the security/scope tradeoffs: same NativeApp:DeviceSecret
// trust boundary as NativeAppAuthService, but a plain completion with no tool-calling.
public sealed class NativeFallbackChatService(HttpClient httpClient)
{
    public async Task<FallbackChatResult> CompleteAsync(
        string deviceSecret, string prompt, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["deviceSecret"] = deviceSecret,
            ["prompt"] = prompt
        });

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsync(
                new Uri(new Uri(AppEndpoints.BaseUrl), "/native/fallback-chat"), content, cancellationToken);
        }
        catch (Exception ex)
        {
            return new FallbackChatResult(false, null, $"Could not reach the server: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "The configured device secret was rejected by the server.",
                HttpStatusCode.GatewayTimeout => "The server's fallback also timed out.",
                _ => $"The server fallback failed ({(int)response.StatusCode})."
            };
            return new FallbackChatResult(false, null, message);
        }

        var body = await response.Content.ReadFromJsonAsync<FallbackChatResponseBody>(cancellationToken);
        return new FallbackChatResult(true, body?.Completion, null);
    }

    private sealed record FallbackChatResponseBody(string? Completion);
}
