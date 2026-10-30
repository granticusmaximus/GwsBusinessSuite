using System.Text;
using System.Text.Json;
using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.App;

// SentinelGPT's agent from a browser on another computer (30-10-26). While this app is open it
// keeps asking the server (with the Workspace tab's session) for model calls from turns started
// where there's no local model, runs each on this Mac's Ollama and sends the reply back. The turn
// itself (the tools, the changes) runs on the server as the same person; this only thinks.
public sealed class SentinelAgentMacRelay(NativeSessionClient session, OllamaClient ollama)
{
    private static readonly HttpClient Local = new() { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromMinutes(5) };
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);
    private bool _started;

    public string Status { get; private set; } = "Not started.";

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        // Once the Workspace tab has had time to sign in.
        await Task.Delay(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                var models = await ollama.ListModelsAsync(CancellationToken.None);
                if (models.Count == 0)
                {
                    Status = "No local models, so other computers can't use this Mac for SentinelGPT.";
                    await Task.Delay(Backoff);
                    continue;
                }

                // A long poll: back within about 25 seconds, with a call or without.
                var (job, error) = await session.TakeAgentChatJobAsync(models);
                if (error is not null)
                {
                    Status = $"SentinelGPT relay paused: {error}";
                    await Task.Delay(Backoff);
                    continue;
                }
                if (job is null) continue;

                var (body, failure) = await RunLocallyAsync(job.PayloadJson);
                await session.ReplyAgentChatJobAsync(job, body, failure);
                Status = $"SentinelGPT: answered a step for another computer at {DateTime.Now:HH:mm}.";
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                Status = $"SentinelGPT relay paused: {ex.Message}";
                await Task.Delay(Backoff);
            }
        }
    }

    // The server's /api/chat request as it is; Ollama's reply, or its error (a model without tools
    // says "does not support tools", and the server then tries the next model).
    private static async Task<(string? Body, string? Error)> RunLocallyAsync(string payloadJson)
    {
        try
        {
            using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            using var response = await Local.PostAsync("api/chat", content);
            var body = await response.Content.ReadAsStringAsync();
            return response.IsSuccessStatusCode ? (body, null) : (null, $"Local Ollama answered HTTP {(int)response.StatusCode}. {ErrorOf(body)}".Trim());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, $"This Mac's Ollama didn't answer: {ex.Message}");
        }
    }

    private static string ErrorOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return body.Length <= 200 ? body : body[..200];
        }
    }
}
