using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public enum OllamaWorkloadPriority
{
    Interactive,
    Background
}

/// <summary>
/// Serializes local model generation and gives queued interactive work precedence over
/// scheduled work. This mirrors the production Ollama one-request limit inside the app,
/// where workload intent is known, instead of relying on Ollama's FIFO queue.
/// </summary>
// One queue per Ollama machine (13-11-26: the Mac first, the AI droplet as backup, each with its own
// queue, so embeddings on the droplet never hold up a chat on the Mac). Priority belongs to the
// request, not the queue, so the ambient priority is shared by every queue. A backup queue passes
// "interactive work arrived" on to the main one (InteractiveArrivedPeer), so background work that
// links InteractiveArrivedToken still yields to someone typing whichever machine is answering.
public sealed class OllamaWorkloadScheduler(TimeProvider? time = null, ILogger<OllamaWorkloadScheduler>? logger = null)
{
    // Set on a backup queue only (OllamaFailoverMonitor). A property, not a constructor parameter,
    // so dependency injection never tries to hand the app-wide queue to itself.
    public OllamaWorkloadScheduler? InteractiveArrivedPeer { get; init; }

    // A wait longer than this is logged, so slowness can be put down to the queue or not (30-10-26).
    private static readonly TimeSpan LoggedWait = TimeSpan.FromSeconds(2);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _interactiveWaiters = [];
    private readonly LinkedList<Waiter> _backgroundWaiters = [];
    private static readonly AsyncLocal<OllamaWorkloadPriority?> _ambientPriority = new();
    private bool _held;
    // When the current holder got the slot (OllamaWatchdogBackgroundService spots a stuck one).
    private DateTimeOffset? _heldSince;
    // Cancelled the moment interactive work starts waiting, then replaced: background work that
    // can stop part-way (World News summaries) links it so someone typing never waits behind it.
    private CancellationTokenSource _interactiveArrived = new();

    public OllamaWorkloadPriority CurrentPriority =>
        _ambientPriority.Value ?? OllamaWorkloadPriority.Interactive;

    // For the shell's status strip (tech-forward T4): whether a generation holds the model, and
    // how many more are queued behind it.
    public (bool Busy, int Waiting) Activity
    {
        get
        {
            lock (_gate)
            {
                return (_held, _interactiveWaiters.Count + _backgroundWaiters.Count);
            }
        }
    }

    public DateTimeOffset? HeldSince
    {
        get
        {
            lock (_gate)
            {
                return _held ? _heldSince : null;
            }
        }
    }

    // Fires when interactive work next queues behind whoever holds the slot.
    public CancellationToken InteractiveArrivedToken
    {
        get
        {
            lock (_gate)
            {
                return _interactiveArrived.Token;
            }
        }
    }

    // Interactive work arrived on another machine's queue: background work holding a lease here
    // and linked to InteractiveArrivedToken stops so the person isn't kept waiting.
    public void SignalInteractiveArrived()
    {
        CancellationTokenSource arrived;
        lock (_gate)
        {
            arrived = _interactiveArrived;
            _interactiveArrived = new CancellationTokenSource();
        }
        arrived.Cancel();
    }

    public IDisposable UseBackgroundPriority()
    {
        var previous = _ambientPriority.Value;
        _ambientPriority.Value = OllamaWorkloadPriority.Background;
        return new PriorityScope(previous);
    }

    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(_ambientPriority.Value ?? OllamaWorkloadPriority.Interactive, cancellationToken);

    public ValueTask<IAsyncDisposable> AcquireAsync(
        OllamaWorkloadPriority priority,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        CancellationTokenSource? arrived = null;
        lock (_gate)
        {
            if (!_held)
            {
                _held = true;
                _heldSince = _time.GetUtcNow();
                return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
            }

            waiter = new Waiter();
            waiter.Node = (priority == OllamaWorkloadPriority.Interactive
                ? _interactiveWaiters
                : _backgroundWaiters).AddLast(waiter);
            if (priority == OllamaWorkloadPriority.Interactive)
            {
                arrived = _interactiveArrived;
                _interactiveArrived = new CancellationTokenSource();
            }
        }
        // Outside the lock: cancellation runs its callbacks inline. Not disposed, since a holder
        // may still link its token (a cancelled source without a timer needs no disposal).
        arrived?.Cancel();
        if (arrived is not null) InteractiveArrivedPeer?.SignalInteractiveArrived();

        return new ValueTask<IAsyncDisposable>(WaitForLeaseAsync(waiter, priority, cancellationToken));
    }

    private async Task<IAsyncDisposable> WaitForLeaseAsync(Waiter waiter, OllamaWorkloadPriority priority, CancellationToken cancellationToken)
    {
        var queued = _time.GetTimestamp();
        using var registration = cancellationToken.Register(
            static state =>
            {
                var cancellation = (CancellationState)state!;
                cancellation.Scheduler.Cancel(cancellation.Waiter, cancellation.Token);
            },
            new CancellationState(this, waiter, cancellationToken));
        var lease = await waiter.Completion.Task.ConfigureAwait(false);
        var waited = _time.GetElapsedTime(queued);
        if (waited >= LoggedWait)
            logger?.LogInformation("An {Priority} Ollama request waited {Seconds:N1} s for its turn", priority.ToString().ToLowerInvariant(), waited.TotalSeconds);
        return lease;
    }

    private void Cancel(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (waiter.Node?.List is null)
            {
                return;
            }

            waiter.Node.List.Remove(waiter.Node);
            waiter.Node = null;
        }

        waiter.Completion.TrySetCanceled(cancellationToken);
    }

    private void Release()
    {
        Waiter? next;
        lock (_gate)
        {
            next = TakeFirst(_interactiveWaiters) ?? TakeFirst(_backgroundWaiters);
            if (next is null)
            {
                _held = false;
                _heldSince = null;
                return;
            }
            _heldSince = _time.GetUtcNow();
        }

        next.Completion.TrySetResult(new Lease(this));
    }

    private static Waiter? TakeFirst(LinkedList<Waiter> queue)
    {
        var node = queue.First;
        if (node is null)
        {
            return null;
        }

        queue.RemoveFirst();
        node.Value.Node = null;
        return node.Value;
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<IAsyncDisposable> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }
    }

    private sealed record CancellationState(
        OllamaWorkloadScheduler Scheduler,
        Waiter Waiter,
        CancellationToken Token);

    private sealed class Lease(OllamaWorkloadScheduler scheduler) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                scheduler.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PriorityScope(OllamaWorkloadPriority? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ambientPriority.Value = previous;
            }
        }
    }
}
