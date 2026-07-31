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
public sealed class OllamaWorkloadScheduler
{
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _interactiveWaiters = [];
    private readonly LinkedList<Waiter> _backgroundWaiters = [];
    private readonly AsyncLocal<OllamaWorkloadPriority?> _ambientPriority = new();
    private bool _held;

    public OllamaWorkloadPriority CurrentPriority =>
        _ambientPriority.Value ?? OllamaWorkloadPriority.Interactive;

    public IDisposable UseBackgroundPriority()
    {
        var previous = _ambientPriority.Value;
        _ambientPriority.Value = OllamaWorkloadPriority.Background;
        return new PriorityScope(this, previous);
    }

    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(_ambientPriority.Value ?? OllamaWorkloadPriority.Interactive, cancellationToken);

    public ValueTask<IAsyncDisposable> AcquireAsync(
        OllamaWorkloadPriority priority,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            if (!_held)
            {
                _held = true;
                return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
            }

            waiter = new Waiter();
            waiter.Node = (priority == OllamaWorkloadPriority.Interactive
                ? _interactiveWaiters
                : _backgroundWaiters).AddLast(waiter);
        }

        return new ValueTask<IAsyncDisposable>(WaitForLeaseAsync(waiter, cancellationToken));
    }

    private async Task<IAsyncDisposable> WaitForLeaseAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static state =>
            {
                var cancellation = (CancellationState)state!;
                cancellation.Scheduler.Cancel(cancellation.Waiter, cancellation.Token);
            },
            new CancellationState(this, waiter, cancellationToken));
        return await waiter.Completion.Task.ConfigureAwait(false);
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
                return;
            }
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

    private sealed class PriorityScope(
        OllamaWorkloadScheduler scheduler,
        OllamaWorkloadPriority? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                scheduler._ambientPriority.Value = previous;
            }
        }
    }
}
