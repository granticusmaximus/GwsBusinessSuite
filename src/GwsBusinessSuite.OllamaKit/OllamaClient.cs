using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GwsBusinessSuite.OllamaKit;

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
        var calls = ToToolCalls(message.ToolCalls);
        var assistant = new OllamaChatMessage("assistant", message.Content ?? string.Empty)
        {
            ToolCalls = message.ToolCalls
        };
        return new OllamaChatResult(message.Content ?? string.Empty, calls, assistant);
    }

    // Streams /api/chat as newline-delimited JSON, one object per line, the final line carrying
    // done:true - same NDJSON shape as the hosted app's OllamaService.GenerateStreamAsync, just
    // against the chat endpoint instead of generate since tool-calling requires it.
    public async IAsyncEnumerable<OllamaChatStreamChunk> ChatStreamAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = new
        {
            model,
            messages,
            tools = tools.Select(tool => tool.ToApiShape()).ToArray(),
            stream = true,
            keep_alive = "30m",
            options = new { num_ctx = 16_384, temperature = 0.2 }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Ollama returned HTTP {(int)response.StatusCode}: {Limit(errorBody, 1_000)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
        {
            OllamaChatApiResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaChatApiResponse>(line);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Ollama returned an invalid streamed chat chunk.", ex);
            }

            if (chunk is null) continue;

            var calls = ToToolCalls(chunk.Message?.ToolCalls);
            if (!string.IsNullOrEmpty(chunk.Message?.Content) || calls.Count > 0)
                yield return new OllamaChatStreamChunk(chunk.Message?.Content ?? string.Empty, calls, false);

            if (chunk.Done)
            {
                yield return new OllamaChatStreamChunk(string.Empty, null, true);
                yield break;
            }
        }
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

    private static IReadOnlyList<OllamaToolCall> ToToolCalls(IReadOnlyList<OllamaApiToolCall>? apiCalls) =>
        apiCalls?
            .Where(call => !string.IsNullOrWhiteSpace(call.Function?.Name))
            .Select(call => new OllamaToolCall(call.Function!.Name, call.Function.Arguments.GetRawText()))
            .ToArray()
        ?? [];

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    private sealed class OllamaChatApiResponse
    {
        [JsonPropertyName("message")]
        public OllamaChatApiMessage? Message { get; init; }

        [JsonPropertyName("done")]
        public bool Done { get; init; }
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
