using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Application.SentinelAgent;

// A browser with no local model borrows the person's Mac (30-10-26, plan Phase B). While their Mac
// app runs, it keeps asking here for model calls (a long poll), runs each on its own Ollama and
// posts the reply back. The agent loop and its tools stay on the server; the Mac only thinks.
// A person's calls only ever go to a Mac signed in as them. In memory: one server, and a call
// waiting on a Mac is only as old as the turn asking.
// Since 05-11-26 it also carries shared work (MacRelayScope): server background jobs on public data,
// such as bill overviews and news hot takes, run on the Admin Mac that asked most recently. They
// wait in a second lane, so the person's own SentinelGPT steps always go first.
public sealed class SentinelAgentRelay(TimeProvider time, TimeSpan? callTimeout = null)
{
    public static readonly TimeSpan PollWait = TimeSpan.FromSeconds(25);
    // A Mac that hasn't asked within this is treated as gone (it asks again as soon as a poll ends).
    public static readonly TimeSpan OnlineFor = TimeSpan.FromSeconds(60);
    // How long one model call may take on the Mac (a big local model's first answer can be slow).
    private readonly TimeSpan _callTimeout = callTimeout ?? TimeSpan.FromMinutes(3);
    private const int MaxModels = 30;

    private readonly ConcurrentDictionary<string, Mac> _macs = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Mac
    {
        public DateTimeOffset LastSeen;
        public IReadOnlyList<string> Models = [];
        // Signed in as an Admin, so it may take shared work.
        public bool TakesSharedWork;
        public readonly Channel<Call> Calls = Channel.CreateUnbounded<Call>();
        public readonly Channel<Call> Shared = Channel.CreateUnbounded<Call>();
        public readonly ConcurrentDictionary<Guid, Call> Waiting = new();
    }

    private sealed record Call(Guid Id, string PayloadJson, TaskCompletionSource<string> Reply);

    // The Mac's models that can run the agent, best first, while it's online; otherwise none.
    public IReadOnlyList<string> ModelsFor(string username) =>
        _macs.TryGetValue(username, out var mac) && IsOnline(mac)
            ? SentinelAgentModels.LocalCandidates(mac.Models)
            : [];

    // Everything this person's Mac has installed, while it's online: Deep Analysis's advisers
    // (DeepAnalysisAdvice) needn't be able to use tools, so they aren't in ModelsFor (07-11-26).
    public IReadOnlyList<string> InstalledFor(string username) =>
        _macs.TryGetValue(username, out var mac) && IsOnline(mac) ? mac.Models.ToList() : [];

    // The online Admin Mac that asked most recently, with everything it has installed, for shared
    // work. Null when there's none.
    public (string Username, IReadOnlyList<string> Models)? SharedWorkMac() =>
        _macs.Where(pair => pair.Value.TakesSharedWork && IsOnline(pair.Value))
            .OrderByDescending(pair => pair.Value.LastSeen)
            .Select(pair => ((string Username, IReadOnlyList<string> Models)?)(pair.Key, pair.Value.Models))
            .FirstOrDefault();

    public Task<string> ChatAsync(string username, string payloadJson, CancellationToken cancellationToken) =>
        SendAsync(_macs.GetOrAdd(username, _ => new Mac()), shared: false, payloadJson, cancellationToken);

    // Shared work on that Admin's Mac (SharedWorkMac), behind any step of the person's own.
    public Task<string> ChatSharedAsync(string username, string payloadJson, CancellationToken cancellationToken) =>
        _macs.TryGetValue(username, out var mac) && mac.TakesSharedWork
            ? SendAsync(mac, shared: true, payloadJson, cancellationToken)
            : throw new InvalidOperationException("That Mac doesn't take shared work.");

    private async Task<string> SendAsync(Mac mac, bool shared, string payloadJson, CancellationToken cancellationToken)
    {
        var call = new Call(Guid.NewGuid(), payloadJson, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        mac.Waiting[call.Id] = call;
        try
        {
            await (shared ? mac.Shared : mac.Calls).Writer.WriteAsync(call, cancellationToken);
            return await call.Reply.Task.WaitAsync(_callTimeout, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Your Mac didn't answer in time. Is the Mac app still open?");
        }
        finally
        {
            mac.Waiting.TryRemove(call.Id, out _);
        }
    }

    private bool IsOnline(Mac mac) => time.GetUtcNow() - mac.LastSeen <= OnlineFor;

    // The Mac's long poll: the next call for this person - their own steps first, then shared work
    // when they're an Admin (takesSharedWork) - or null after PollWait.
    public async Task<(Guid Id, string PayloadJson)?> TakeAsync(string username, IEnumerable<string> models, CancellationToken cancellationToken,
        bool takesSharedWork = false)
    {
        var mac = _macs.GetOrAdd(username, _ => new Mac());
        mac.Models = models.Take(MaxModels).ToList();
        mac.TakesSharedWork = takesSharedWork;
        mac.LastSeen = time.GetUtcNow();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(PollWait);
        try
        {
            while (true)
            {
                if (mac.Calls.Reader.TryRead(out var call) || (takesSharedWork && mac.Shared.Reader.TryRead(out call)))
                {
                    // A turn or job that gave up (stopped, timed out) no longer wants its call.
                    if (mac.Waiting.ContainsKey(call.Id)) return (call.Id, call.PayloadJson);
                    continue;
                }
                var own = mac.Calls.Reader.WaitToReadAsync(wait.Token).AsTask();
                await (takesSharedWork ? (Task)Task.WhenAny(own, mac.Shared.Reader.WaitToReadAsync(wait.Token).AsTask()) : own);
                wait.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            mac.LastSeen = time.GetUtcNow();
        }
    }

    // The Mac's reply (Ollama's /api/chat body) or what went wrong there. False when nothing is
    // waiting for it any more.
    public bool Complete(string username, Guid callId, string? body, string? error)
    {
        if (!_macs.TryGetValue(username, out var mac) || !mac.Waiting.TryGetValue(callId, out var call)) return false;
        return error is null
            ? call.Reply.TrySetResult(body ?? string.Empty)
            : call.Reply.TrySetException(new InvalidOperationException(error));
    }
}

// The agent's model, when it's the person's Mac through the relay. Same request as the server's
// and the browser's (OllamaChatPayload), so the loop can't tell where the model runs.
public sealed class RelayedOllamaService(SentinelAgentRelay relay, string username) : IOllamaService
{
    public async Task<OllamaChatResponse> ChatAsync(
        string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition>? tools = null, CancellationToken ct = default)
    {
        var payload = OllamaChatPayload.Build(model, messages, tools, "30m").ToJsonString();
        return OllamaChatPayload.Parse(await relay.ChatAsync(username, payload, ct));
    }

    public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        throw new NotSupportedException("Only the agent's chat goes to the Mac.");

    public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("Only the agent's chat goes to the Mac.");
#pragma warning disable CS0162 // An iterator needs a yield.
        yield break;
#pragma warning restore CS0162
    }

    public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(relay.ModelsFor(username).ToList());

    public Task PullModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException("Only the agent's chat goes to the Mac.");

    public Task DeleteModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException("Only the agent's chat goes to the Mac.");

    public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
        throw new NotSupportedException("Only the agent's chat goes to the Mac.");
}
