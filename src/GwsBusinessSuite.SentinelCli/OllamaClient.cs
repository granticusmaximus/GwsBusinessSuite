using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GwsBusinessSuite.SentinelCli;

public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public OllamaClient(Uri baseUri, HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _http.BaseAddress ??= baseUri;
    }

    public async Task<OllamaChatResult> ChatAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model,
            messages,
            tools = tools.Select(tool => tool.ToApiShape()).ToArray(),
            stream = false,
            keep_alive = "30m",
            options = new { num_ctx = 16_384, temperature = 0.2 }
        };

        using var response = await _http.PostAsJsonAsync("api/chat", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Ollama returned HTTP {(int)response.StatusCode}: {Limit(body, 1_000)}");

        OllamaChatApiResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OllamaChatApiResponse>(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Ollama returned an invalid chat response.", ex);
        }

        var message = parsed?.Message
            ?? throw new InvalidOperationException("Ollama returned no assistant message.");
        var calls = message.ToolCalls?
            .Where(call => !string.IsNullOrWhiteSpace(call.Function?.Name))
            .Select(call => new OllamaToolCall(call.Function!.Name, call.Function.Arguments.Clone()))
            .ToArray()
            ?? [];
        var assistant = new OllamaChatMessage("assistant", message.Content ?? string.Empty)
        {
            ToolCalls = message.ToolCalls
        };
        return new OllamaChatResult(message.Content ?? string.Empty, calls, assistant);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("api/tags", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama is unavailable at {_http.BaseAddress}.");
        var result = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(cancellationToken);
        return result?.Models?
            .Select(model => model.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    private sealed class OllamaChatApiResponse
    {
        [JsonPropertyName("message")]
        public OllamaChatApiMessage? Message { get; init; }
    }

    private sealed class OllamaChatApiMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("tool_calls")]
        public IReadOnlyList<OllamaApiToolCall>? ToolCalls { get; init; }
    }

    private sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public IReadOnlyList<OllamaTag>? Models { get; init; }
    }

    private sealed class OllamaTag
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }
}
