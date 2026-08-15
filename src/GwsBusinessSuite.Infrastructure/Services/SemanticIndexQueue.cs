using System.Threading.Channels;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SemanticIndexQueue
{
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public void RequestReconciliation() => _requests.Writer.TryWrite(true);
    public async Task WaitAsync(TimeSpan maximumDelay, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maximumDelay);
        try
        {
            await _requests.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The periodic deadline is itself a reconciliation request.
        }
    }
}

// Save hooks stay fast: they only signal the bounded queue. Embedding happens after the
// transaction in SemanticIndexBackgroundService, and a periodic reconciliation repairs the
// unlikely race where a worker observes a source just before its commit becomes visible.
public sealed class SemanticIndexSaveChangesInterceptor(SemanticIndexQueue queue) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        SignalIfRelevant(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        SignalIfRelevant(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void SignalIfRelevant(DbContext? context)
    {
        if (context?.ChangeTracker.Entries().Any(entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && entry.Entity is WikiPage or WikiDatabase or WikiDatabaseProperty or WikiDatabaseRow
                or Contact or ContactActivity or Deal or CmsPage) == true)
        {
            queue.RequestReconciliation();
        }
    }
}
