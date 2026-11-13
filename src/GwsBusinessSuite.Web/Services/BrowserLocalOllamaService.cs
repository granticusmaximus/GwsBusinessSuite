using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GwsBusinessSuite.Application.Abstractions;
using Microsoft.JSInterop;

namespace GwsBusinessSuite.Web.Services;

public sealed record LocalOllamaStatus(bool IsReachable, IReadOnlyList<string> Models, string? Error);

/// <summary>
/// An <see cref="IOllamaService"/> whose model calls run on the Ollama of the machine the
/// browser is on, relayed through this circuit's JS interop (wwwroot/js/local-ollama.js). Handing
/// it to Content Studio keeps prompt-building, compile-and-repair, and persistence on the server
/// while the expensive generation itself happens on the user's own hardware. Only text
/// generation and tool-calling chat (SentinelGPT's agent) are relayed; everything else is deliberately unsupported.
/// </summary>
public sealed class BrowserLocalOllamaService(IJSRuntime js) : IOllamaService, IAsyncDisposable
{
    public const string DefaultBaseUrl = "http://localhost:11434";

    private readonly ConcurrentDictionary<string, Channel<string>> _requests = new();
    private IJSObjectReference? _module;
    private DotNetObjectReference<BrowserLocalOllamaService>? _selfReference;

    public const string ServerModelOnlyMessage = "SentinelGPT runs on the server's model; models on this computer aren't used.";

    public string BaseUrl { get; init; } = DefaultBaseUrl;

    // True when the server's model is the only source (ServerAiOptions.ServerModelOnly). Nothing
    // runs on this computer then: with Server set (panels that have no server path of their own),
    // every call goes to the server's model; without it, the probe reports no local Ollama and the
    // caller uses its own server path.
    public bool Off { get; init; }

    public IOllamaService? Server { get; init; }

    // Every panel makes its local client here, so one setting decides whether local models are used.
    public static BrowserLocalOllamaService For(IJSRuntime js, ServerAiOptions serverAi, IOllamaService? server = null) =>
        new(js) { Off = serverAi.ServerModelOnly, Server = serverAi.ServerModelOnly ? server : null };

    private IOllamaService ServerModel => Server ?? throw new InvalidOperationException(ServerModelOnlyMessage);

    public async Task<LocalOllamaStatus> ProbeAsync(CancellationToken ct = default)
    {
        if (Off)
        {
            if (Server is null) return new LocalOllamaStatus(false, [], ServerModelOnlyMessage);
            try
            {
                var models = await Server.ListModelsAsync(ct);
                return new LocalOllamaStatus(true, models.ToList(), null);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new LocalOllamaStatus(false, [], $"The server's model couldn't be reached: {ex.Message}");
            }
        }
        var module = await GetModuleAsync();
        var result = await module.InvokeAsync<ProbeResult>("probe", ct, BaseUrl);
        return new LocalOllamaStatus(result.Ok, result.Models ?? [], result.Error);
    }

    // Called before starting a generation so an unreachable Ollama fails in seconds with a fix,
    // rather than after the server has already scored offers and built the prompt.
    public async Task EnsureReachableAsync(CancellationToken ct = default)
    {
        var status = await ProbeAsync(ct);
        if (!status.IsReachable)
        {
            throw new InvalidOperationException(status.Error ?? $"Local Ollama at {BaseUrl} is not reachable.");
        }
    }

    public async Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        if (Off) return await ServerModel.GenerateAsync(model, systemPrompt, userPrompt, ct);
        var builder = new System.Text.StringBuilder();
        await foreach (var fragment in StreamCoreAsync(model, systemPrompt, userPrompt, null, ct))
        {
            builder.Append(fragment);
        }

        return builder.ToString();
    }

    // Ollama's JSON-only mode (its "format" option), for answers that get parsed rather than read.
    public async Task<string> GenerateJsonAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        // The server client has no JSON mode, so the instruction goes in the prompt; callers parse leniently.
        if (Off) return await ServerModel.GenerateAsync(model, systemPrompt + "\n\nAnswer with JSON only: no prose, no code fences.", userPrompt, ct);
        var builder = new System.Text.StringBuilder();
        await foreach (var fragment in StreamCoreAsync(model, systemPrompt, userPrompt, null, ct, null, "json"))
        {
            builder.Append(fragment);
        }

        return builder.ToString();
    }

    // A vision call: the images (base64, no data: prefix) go with the prompt, and json=true asks
    // Ollama for a JSON-only answer (its "format" option).
    public async Task<string> GenerateWithImagesAsync(string model, string systemPrompt, string userPrompt,
        IReadOnlyList<string> base64Images, bool json, CancellationToken ct = default)
    {
        if (Off) throw new InvalidOperationException("Reading images needs a vision model, and SentinelGPT runs only on the server's model, which can't read images.");
        var builder = new System.Text.StringBuilder();
        await foreach (var fragment in StreamCoreAsync(model, systemPrompt, userPrompt, null, ct, base64Images, json ? "json" : null))
        {
            builder.Append(fragment);
        }

        return builder.ToString();
    }

    public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        Off ? ServerModel.GenerateStreamAsync(model, systemPrompt, userPrompt, ct) : StreamCoreAsync(model, systemPrompt, userPrompt, null, ct);

    public IAsyncEnumerable<string> GenerateStreamAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        int maxOutputTokens,
        CancellationToken ct = default) =>
        Off ? ServerModel.GenerateStreamAsync(model, systemPrompt, userPrompt, maxOutputTokens, ct) : StreamCoreAsync(model, systemPrompt, userPrompt, maxOutputTokens, ct);

    public async Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var status = await ProbeAsync(ct);
        return status.IsReachable
            ? status.Models.ToArray()
            : throw new InvalidOperationException(status.Error ?? "Local Ollama is not reachable.");
    }

    public Task PullModelAsync(string model, CancellationToken ct = default) =>
        throw new NotSupportedException("Install local models from the Mac app or with `ollama pull`.");

    public Task DeleteModelAsync(string model, CancellationToken ct = default) =>
        throw new NotSupportedException("Remove local models from the Mac app or with `ollama rm`.");

    // A tool-calling round on this computer's Ollama, for SentinelGPT's agent: the request is the
    // server's own (OllamaChatPayload), and the reply comes back through OnChunk like a generation.
    public async Task<OllamaChatResponse> ChatAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition>? tools = null,
        CancellationToken ct = default)
    {
        if (Off) return await ServerModel.ChatAsync(model, messages, tools, ct);
        var module = await GetModuleAsync();
        _selfReference ??= DotNetObjectReference.Create(this);
        var requestId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _requests[requestId] = channel;
        var completed = false;
        try
        {
            var payload = OllamaChatPayload.Build(model, messages, tools, "30m").ToJsonString();
            await module.InvokeVoidAsync("startChat", ct, requestId, BaseUrl, payload, _selfReference);
            var body = new System.Text.StringBuilder();
            await foreach (var piece in channel.Reader.ReadAllAsync(ct))
            {
                body.Append(piece);
            }

            completed = true;
            return OllamaChatPayload.Parse(body.ToString());
        }
        finally
        {
            _requests.TryRemove(requestId, out _);
            if (!completed)
            {
                await TryAbortAsync(module, requestId);
            }
        }
    }

    public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
        throw new NotSupportedException("Image generation is not relayed to the local Ollama.");

    [JSInvokable]
    public void OnChunk(string requestId, string text)
    {
        if (_requests.TryGetValue(requestId, out var channel))
        {
            channel.Writer.TryWrite(text);
        }
    }

    [JSInvokable]
    public void OnCompleted(string requestId)
    {
        if (_requests.TryGetValue(requestId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    [JSInvokable]
    public void OnFailed(string requestId, string message)
    {
        if (_requests.TryGetValue(requestId, out var channel))
        {
            channel.Writer.TryComplete(new InvalidOperationException(message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (requestId, channel) in _requests)
        {
            channel.Writer.TryComplete(new OperationCanceledException("The page that started this local generation was closed."));
            if (_module is not null)
            {
                await TryAbortAsync(_module, requestId);
            }
        }

        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already gone - nothing to release on the browser side.
            }
        }

        _selfReference?.Dispose();
    }

    private async IAsyncEnumerable<string> StreamCoreAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        int? maxOutputTokens,
        [EnumeratorCancellation] CancellationToken ct,
        IReadOnlyList<string>? images = null,
        string? format = null)
    {
        var module = await GetModuleAsync();
        _selfReference ??= DotNetObjectReference.Create(this);

        var requestId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _requests[requestId] = channel;
        var completed = false;
        try
        {
            await module.InvokeVoidAsync(
                "startGenerate",
                ct,
                requestId,
                BaseUrl,
                new { model, system = systemPrompt, prompt = userPrompt, numPredict = maxOutputTokens, images, format },
                _selfReference);

            await foreach (var fragment in channel.Reader.ReadAllAsync(ct))
            {
                yield return fragment;
            }

            completed = true;
        }
        finally
        {
            _requests.TryRemove(requestId, out _);
            if (!completed)
            {
                await TryAbortAsync(module, requestId);
            }
        }
    }

    private async Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/local-ollama.js");

    private static async Task TryAbortAsync(IJSObjectReference module, string requestId)
    {
        try
        {
            await module.InvokeVoidAsync("abort", requestId);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or TaskCanceledException or ObjectDisposedException)
        {
            // Best effort: if the browser side is gone, so is the request it would cancel.
        }
    }

    private sealed record ProbeResult(bool Ok, string[]? Models, string? Error);
}
