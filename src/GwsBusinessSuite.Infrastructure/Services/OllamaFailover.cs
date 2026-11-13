using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// SentinelGPT's two machines (13-11-26, Grant): the Mac first (Apple Silicon, the full gemma4
// SentinelGPT, reached over Tailscale) and the AI droplet second (always on, a model that fits it).
//
// OllamaFailoverMonitor asks the Mac every few seconds whether it's there. Two misses in a row
// mark it down; three answers in a row bring it back, so a flaky link doesn't flip back and forth.
// A request that fails to connect marks it down at once. Going down also cancels every request
// still waiting on the Mac (PrimaryLostToken), so a Mac that falls asleep mid-answer costs seconds,
// not a two-hour timeout: FailoverOllamaService runs that request again on the droplet.
//
// Embeddings live on the droplet: one machine, one vector space. They only move to the Mac when
// both machines hold the identical embedding model (same digest), because vectors from two builds
// of a model can't be compared.
public sealed class OllamaFailoverMonitor : BackgroundService
{
    public const string PrimaryClient = "ollama-primary";
    public const string SecondaryClient = "ollama-secondary";
    public const string ProbeClient = "ollama-probe";

    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
    public const int DownAfterMisses = 2;
    public const int UpAfterAnswers = 3;
    // Model lists (with digests) are re-read this often, and on every change of state.
    private const int TagsEveryProbes = 12;

    private readonly IHttpClientFactory _clients;
    private readonly ILogger<OllamaFailoverMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private CircuitState _primary = CircuitState.Initial;
    private bool _secondaryUp = true;
    private CancellationTokenSource _primaryLost = new();
    private IReadOnlyDictionary<string, string> _primaryDigests = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _secondaryDigests = new Dictionary<string, string>();
    private int _probes;

    public OllamaFailoverMonitor(IHttpClientFactory clients, OllamaWorkloadScheduler primaryLane, ILogger<OllamaFailoverMonitor> logger,
        ILogger<OllamaWorkloadScheduler>? laneLogger = null, TimeProvider? time = null)
    {
        _clients = clients;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        PrimaryLane = primaryLane;
        SecondaryLane = new OllamaWorkloadScheduler(_time, laneLogger) { InteractiveArrivedPeer = primaryLane };
    }

    // Each machine's own queue: the existing app-wide one for the Mac, a second for the droplet.
    public OllamaWorkloadScheduler PrimaryLane { get; }
    public OllamaWorkloadScheduler SecondaryLane { get; }

    public bool PrimaryUp { get { lock (_gate) return _primary.Up; } }
    public bool SecondaryUp { get { lock (_gate) return _secondaryUp; } }
    public DateTimeOffset? PrimaryChangedAt { get { lock (_gate) return _primary.ChangedAt; } }

    // Cancelled the moment the Mac is marked down; replaced when it comes back.
    public CancellationToken PrimaryLostToken { get { lock (_gate) return _primaryLost.Token; } }

    // Busy and queued across both machines, for the status strip.
    public (bool Busy, int Waiting) Activity
    {
        get
        {
            var (primaryBusy, primaryWaiting) = PrimaryLane.Activity;
            var (secondaryBusy, secondaryWaiting) = SecondaryLane.Activity;
            return (primaryBusy || secondaryBusy, primaryWaiting + secondaryWaiting);
        }
    }

    // True when both machines hold the same build of an embedding model, so vectors match.
    public bool SameModelOnBoth(string model)
    {
        lock (_gate)
        {
            return Digest(_primaryDigests, model) is { } a && Digest(_secondaryDigests, model) is { } b && a == b;
        }
    }

    // A request couldn't reach the Mac: it's down now, without waiting for the next probe.
    public void ReportPrimaryFailure(Exception error)
    {
        CancellationTokenSource? lost = null;
        lock (_gate)
        {
            if (!_primary.Up) return;
            _primary = CircuitState.ForcedDown(_time.GetUtcNow());
            lost = _primaryLost;
        }
        _logger.LogWarning(error, "SentinelGPT's Mac stopped answering; the AI droplet takes over until it's back");
        lost.Cancel();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ProbeInterval, _time);
        do
        {
            try { await ProbeOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Ollama failover probe failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // One round: is each machine answering? Public for tests.
    public async Task ProbeOnceAsync(CancellationToken ct)
    {
        var primaryAnswered = await AnswersAsync(PrimaryClient, ct);
        var secondaryAnswered = await AnswersAsync(SecondaryClient, ct);
        CancellationTokenSource? lost = null;
        bool? cameBack = null;
        bool changed;
        lock (_gate)
        {
            var before = _primary.Up;
            _primary = _primary.After(primaryAnswered, _time.GetUtcNow());
            changed = before != _primary.Up || _secondaryUp != secondaryAnswered;
            _secondaryUp = secondaryAnswered;
            if (before && !_primary.Up)
            {
                lost = _primaryLost;
                cameBack = false;
            }
            else if (!before && _primary.Up)
            {
                _primaryLost = new CancellationTokenSource();
                cameBack = true;
            }
        }
        if (cameBack == false)
        {
            _logger.LogWarning("SentinelGPT's Mac stopped answering; the AI droplet takes over until it's back");
            lost!.Cancel();
        }
        else if (cameBack == true) _logger.LogInformation("SentinelGPT's Mac is back; it answers first again");
        if (!secondaryAnswered && changed) _logger.LogWarning("The AI droplet isn't answering");

        if (changed || _probes++ % TagsEveryProbes == 0)
        {
            var primaryDigests = primaryAnswered ? await DigestsAsync(PrimaryClient, ct) : null;
            var secondaryDigests = secondaryAnswered ? await DigestsAsync(SecondaryClient, ct) : null;
            lock (_gate)
            {
                if (primaryDigests is not null) _primaryDigests = primaryDigests;
                if (secondaryDigests is not null) _secondaryDigests = secondaryDigests;
            }
        }
    }

    private async Task<bool> AnswersAsync(string client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await _clients.CreateClient(client).GetAsync("api/version", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> DigestsAsync(string client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return ParseDigests(await _clients.CreateClient(client).GetStringAsync("api/tags", timeout.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // /api/tags: {"models":[{"name":"embeddinggemma:latest","digest":"85462619..."}]}
    public static IReadOnlyDictionary<string, string> ParseDigests(string json)
    {
        var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return digests;
        foreach (var model in models.EnumerateArray())
        {
            if (model.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n
                && model.TryGetProperty("digest", out var digest) && digest.GetString() is { Length: > 0 } d)
                digests[n] = d;
        }
        return digests;
    }

    private static string? Digest(IReadOnlyDictionary<string, string> digests, string model) =>
        digests.TryGetValue(model, out var exact) ? exact
        : !model.Contains(':') && digests.TryGetValue(model + ":latest", out var latest) ? latest
        : null;

    public override void Dispose()
    {
        _primaryLost.Dispose();
        base.Dispose();
    }

    // The Mac's state, with hysteresis: down after DownAfterMisses misses in a row, back after
    // UpAfterAnswers answers in a row. Starts up, so the first request after a restart goes to the
    // Mac (and finds out in seconds if it isn't there).
    public readonly record struct CircuitState(bool Up, int Misses, int Answers, DateTimeOffset? ChangedAt)
    {
        public static CircuitState Initial => new(true, 0, 0, null);

        public static CircuitState ForcedDown(DateTimeOffset at) => new(false, DownAfterMisses, 0, at);

        public CircuitState After(bool answered, DateTimeOffset at) => (Up, answered) switch
        {
            (true, true) => this with { Misses = 0 },
            (true, false) when Misses + 1 >= DownAfterMisses => new(false, Misses + 1, 0, at),
            (true, false) => this with { Misses = Misses + 1 },
            (false, true) when Answers + 1 >= UpAfterAnswers => new(true, 0, Answers + 1, at),
            (false, true) => this with { Answers = Answers + 1 },
            (false, false) => this with { Answers = 0 },
        };
    }
}

// IOllamaService over the two machines (see OllamaFailoverMonitor). Generation, chat, images and
// model lists go to the Mac while it's up and run again on the droplet when the Mac can't answer.
// Embeddings go to the droplet (the Mac only when it holds the identical model). Pulling and
// deleting models manage the droplet, the server's own Ollama.
public sealed class FailoverOllamaService(
    IOllamaService primary,
    IOllamaService secondary,
    OllamaFailoverMonitor monitor,
    ILogger<FailoverOllamaService> logger) : IOllamaService
{
    public const string MidAnswerMessage =
        "SentinelGPT's Mac stopped answering part-way through that answer. Ask again: the backup answers now.";

    public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        MacFirstAsync((ollama, token) => ollama.GenerateAsync(model, systemPrompt, userPrompt, token), ct);

    public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, int numCtx, CancellationToken ct = default) =>
        MacFirstAsync((ollama, token) => ollama.GenerateAsync(model, systemPrompt, userPrompt, numCtx, token), ct);

    public Task WarmModelAsync(string model, CancellationToken ct = default) =>
        MacFirstAsync(async (ollama, token) => { await ollama.WarmModelAsync(model, token); return true; }, ct);

    public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        MacFirstStreamAsync((ollama, token) => ollama.GenerateStreamAsync(model, systemPrompt, userPrompt, token), ct);

    public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken ct = default) =>
        MacFirstStreamAsync((ollama, token) => ollama.GenerateStreamAsync(model, systemPrompt, userPrompt, maxOutputTokens, token), ct);

    public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
        MacFirstAsync((ollama, token) => ollama.ListModelsAsync(token), ct);

    public Task PullModelAsync(string model, CancellationToken ct = default) => secondary.PullModelAsync(model, ct);

    public Task DeleteModelAsync(string model, CancellationToken ct = default) => secondary.DeleteModelAsync(model, ct);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        try
        {
            return await secondary.EmbedAsync(model, inputs, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && monitor.PrimaryUp && monitor.SameModelOnBoth(model))
        {
            logger.LogDebug(ex, "Embedding on the Mac: the droplet didn't answer and both hold the same {Model}", model);
            return await primary.EmbedAsync(model, inputs, ct);
        }
    }

    public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
        MacFirstAsync((ollama, token) => ollama.GenerateImageAsync(model, prompt, token), ct);

    public Task<OllamaChatResponse> ChatAsync(string model, IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition>? tools = null, CancellationToken ct = default) =>
        MacFirstAsync((ollama, token) => ollama.ChatAsync(model, messages, tools, token), ct);

    private async Task<T> MacFirstAsync<T>(Func<IOllamaService, CancellationToken, Task<T>> call, CancellationToken ct)
    {
        if (monitor.PrimaryUp)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, monitor.PrimaryLostToken);
            try
            {
                return await call(primary, linked.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not NotSupportedException)
            {
                Fallback(ex);
            }
        }
        return await call(secondary, ct);
    }

    private async IAsyncEnumerable<string> MacFirstStreamAsync(Func<IOllamaService, CancellationToken, IAsyncEnumerable<string>> call,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (monitor.PrimaryUp)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, monitor.PrimaryLostToken);
            var stream = call(primary, linked.Token).GetAsyncEnumerator(linked.Token);
            var yielded = false;
            var fellBack = false;
            try
            {
                while (true)
                {
                    bool more;
                    try
                    {
                        more = await stream.MoveNextAsync();
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is not NotSupportedException)
                    {
                        Fallback(ex);
                        // Words already shown can't be taken back, so a half-finished answer says so.
                        if (yielded) throw new InvalidOperationException(MidAnswerMessage, ex);
                        fellBack = true;
                        break;
                    }
                    if (!more) break;
                    yielded = true;
                    yield return stream.Current;
                }
            }
            finally
            {
                try { await stream.DisposeAsync(); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { }
            }
            if (!fellBack) yield break;
        }
        await foreach (var piece in call(secondary, ct).WithCancellation(ct)) yield return piece;
    }

    // Runs again on the droplet. A connection failure (or the monitor losing the Mac) marks the Mac
    // down; an error answer from a Mac that's there (a model it lacks, say) doesn't.
    private void Fallback(Exception ex)
    {
        if (IsConnectionFailure(ex)) monitor.ReportPrimaryFailure(ex);
        else logger.LogInformation(ex, "SentinelGPT's Mac couldn't answer that request; the AI droplet answers it");
    }

    public static bool IsConnectionFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: null } or IOException or OperationCanceledException
        || ex.InnerException is { } inner && IsConnectionFailure(inner);
}
