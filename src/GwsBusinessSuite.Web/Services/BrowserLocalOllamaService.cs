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
/// generation is relayed; everything else is deliberately unsupported.
/// </summary>
public sealed class BrowserLocalOllamaService(IJSRuntime js) : IOllamaService, IAsyncDisposable
{
    public const string DefaultBaseUrl = "http://localhost:11434";

    private readonly ConcurrentDictionary<string, Channel<string>> _requests = new();
    private IJSObjectReference? _module;
    private DotNetObjectReference<BrowserLocalOllamaService>? _selfReference;

    public string BaseUrl { get; init; } = DefaultBaseUrl;

    public async Task<LocalOllamaStatus> ProbeAsync(CancellationToken ct = default)
    {
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
        var builder = new System.Text.StringBuilder();
        await foreach (var fragment in StreamCoreAsync(model, systemPrompt, userPrompt, null, ct))
        {
            builder.Append(fragment);
        }

        return builder.ToString();
    }

    public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        StreamCoreAsync(model, systemPrompt, userPrompt, null, ct);

    public IAsyncEnumerable<string> GenerateStreamAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        int maxOutputTokens,
        CancellationToken ct = default) =>
        StreamCoreAsync(model, systemPrompt, userPrompt, maxOutputTokens, ct);

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
        [EnumeratorCancellation] CancellationToken ct)
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
                new { model, system = systemPrompt, prompt = userPrompt, numPredict = maxOutputTokens },
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
