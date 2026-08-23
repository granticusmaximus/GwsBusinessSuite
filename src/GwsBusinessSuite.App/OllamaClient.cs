using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GwsBusinessSuite.App;

// Talks only to Ollama on this same Mac - never the hosted GWS server - so SentinelGPT in this
// tab keeps working offline and never sends local conversation content to admin.gwsapp.net.
public sealed class OllamaClient : IDisposable
{
    private static readonly Uri LoopbackBaseUri = new("http://127.0.0.1:11434/");
    private readonly HttpClient _http = new() { BaseAddress = LoopbackBaseUri, Timeout = TimeSpan.FromMinutes(5) };

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("api/tags", cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TagsResponse>(cancellationToken: cancellationToken);
        return result?.Models?
            .Select(model => model.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];
    }

    public async Task<string> ChatAsync(string model, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var payload = new { model, messages, stream = false, keep_alive = "30m" };
        using var response = await _http.PostAsJsonAsync("api/chat", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatApiResponse>(cancellationToken: cancellationToken);
        return result?.Message?.Content ?? string.Empty;
    }

    public void Dispose() => _http.Dispose();

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")]
        public IReadOnlyList<TagModel>? Models { get; init; }
    }

    private sealed class TagModel
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    private sealed class ChatApiResponse
    {
        [JsonPropertyName("message")]
        public ChatApiMessage? Message { get; init; }
    }

    private sealed class ChatApiMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }
}

public sealed class ChatMessage(string role, string content)
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = role;

    [JsonPropertyName("content")]
    public string Content { get; init; } = content;
}
