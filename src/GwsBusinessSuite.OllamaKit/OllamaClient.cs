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
        CancellationToken cancellationToken,
        bool? think = null)
    {
        var payload = BuildChatPayload(model, messages, tools, stream: false, think);

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
        return new OllamaChatResult(
            message.Content ?? string.Empty, calls, assistant, message.Thinking ?? string.Empty);
    }

    // Streams /api/chat as newline-delimited JSON, one object per line, the final line carrying
    // done:true - same NDJSON shape as the hosted app's OllamaService.GenerateStreamAsync, just
    // against the chat endpoint instead of generate since tool-calling requires it.
    public async IAsyncEnumerable<OllamaChatStreamChunk> ChatStreamAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        bool? think = null)
    {
        var payload = BuildChatPayload(model, messages, tools, stream: true, think);

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
            var thinking = chunk.Message?.Thinking ?? string.Empty;
            if (!string.IsNullOrEmpty(chunk.Message?.Content) || calls.Count > 0 || thinking.Length > 0)
                yield return new OllamaChatStreamChunk(chunk.Message?.Content ?? string.Empty, calls, false, thinking);

            if (chunk.Done)
            {
                yield return new OllamaChatStreamChunk(string.Empty, null, true);
                yield break;
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
        (await ListModelDetailsAsync(cancellationToken)).Select(model => model.Name).ToArray();

    // The capability-aware companion to ListModelsAsync, for hosts that let a human pick a model
    // and therefore have to keep unusable ones (embedding-only, image-only) out of the list -
    // see OllamaModelInfo for why Ollama makes that the caller's problem.
    public async Task<IReadOnlyList<OllamaModelInfo>> ListModelDetailsAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("api/tags", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama is unavailable at {_http.BaseAddress}.");
        var result = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(cancellationToken);
        return result?.Models?
            .Where(model => !string.IsNullOrWhiteSpace(model.Name))
            .DistinctBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .Select(model => new OllamaModelInfo(
                model.Name, model.Details?.ParameterSize, model.Capabilities ?? []))
            .ToArray()
            ?? [];
    }

    // Downloads a model, streaming progress. The HTTP equivalent of `ollama pull`, which matters
    // because the sandboxed Mac app cannot spawn the ollama binary at all (App Sandbox denies
    // process execution) - it has the network-client entitlement and nothing else, so this is the
    // only route by which it can install a model itself instead of sending the user to Terminal.
    public async IAsyncEnumerable<OllamaProgress> PullModelAsync(
        string model, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        await foreach (var progress in StreamProgressAsync(
            "api/pull", new { model, stream = true }, cancellationToken))
        {
            yield return progress;
        }
    }

    // Builds a named profile from a parsed Modelfile - the HTTP equivalent of `ollama create -f`.
    // See OllamaModelfile for why the whole-file "modelfile" field can't be used any more.
    public async IAsyncEnumerable<OllamaProgress> CreateModelAsync(
        string model,
        OllamaModelfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(profile);

        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["from"] = profile.From,
            ["stream"] = true
        };
        if (!string.IsNullOrWhiteSpace(profile.System)) payload["system"] = profile.System;
        if (profile.Parameters.Count > 0) payload["parameters"] = ToTypedParameters(profile.Parameters);
        if (profile.Messages.Count > 0)
            payload["messages"] = profile.Messages
                .Select(message => new { role = message.Role, content = message.Content })
                .ToArray();

        await foreach (var progress in StreamProgressAsync("api/create", payload, cancellationToken))
            yield return progress;
    }

    // Modelfile parameters are all text once parsed, but /api/create wants them typed the way
    // the model's own config expects (num_ctx as a number, not "16384"), so anything that looks
    // numeric or boolean is converted back rather than sent as a string.
    private static Dictionary<string, object> ToTypedParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var typed = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            typed[key] = long.TryParse(value, out var integer) ? integer
                : double.TryParse(value, out var number) ? number
                : bool.TryParse(value, out var flag) ? flag
                : value;
        }
        return typed;
    }

    private async IAsyncEnumerable<OllamaProgress> StreamProgressAsync(
        string route, object payload, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(payload) };
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
            OllamaProgressApiResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaProgressApiResponse>(line);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Ollama returned an invalid progress chunk.", ex);
            }
            if (chunk is null) continue;

            // Ollama reports a mid-stream failure in the body with a 200 already on the wire, so
            // this is the only place a failed pull can be noticed at all.
            if (!string.IsNullOrWhiteSpace(chunk.Error))
                throw new InvalidOperationException($"Ollama reported: {chunk.Error}");

            yield return new OllamaProgress(chunk.Status ?? string.Empty, chunk.Completed, chunk.Total);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }

    // One shape for both the streaming and non-streaming calls so they can't drift apart on
    // options/keep_alive. "think" is omitted entirely when null rather than sent as false, so a
    // caller that hasn't formed an opinion leaves each model on its own default. Sending
    // think:false is safe even on models that don't report the "thinking" capability - verified
    // against llama3.2 and qwen2.5-coder, which simply ignore it - and reasoning models that
    // insist on thinking anyway (deepseek-r1) also accept it without erroring.
    private static object BuildChatPayload(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition> tools,
        bool stream,
        bool? think)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            // An empty array is meaningful, not a no-op to be omitted: it is the one way to chat
            // with a model that lacks the "tools" capability, which rejects a *populated* tools
            // array with HTTP 400 for the whole request.
            ["tools"] = tools.Select(tool => tool.ToApiShape()).ToArray(),
            ["stream"] = stream,
            ["keep_alive"] = "30m",
            ["options"] = new { num_ctx = 16_384, temperature = 0.2 }
        };
        if (think is not null)
            payload["think"] = think.Value;
        return payload;
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

        // Reasoning models put their deliberation here and leave Content empty until they're
        // done. Parsing it is what keeps a thinking-only chunk from looking like an empty
        // response - deepseek-r1 answers "say hi" with content:"" and a populated thinking
        // field, which callers used to see as a model that returned nothing at all.
        [JsonPropertyName("thinking")]
        public string? Thinking { get; init; }

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

        [JsonPropertyName("capabilities")]
        public IReadOnlyList<string>? Capabilities { get; init; }

        [JsonPropertyName("details")]
        public OllamaTagDetails? Details { get; init; }
    }

    private sealed class OllamaTagDetails
    {
        [JsonPropertyName("parameter_size")]
        public string? ParameterSize { get; init; }
    }

    private sealed class OllamaProgressApiResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("completed")]
        public long Completed { get; init; }

        [JsonPropertyName("total")]
        public long Total { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }
}
