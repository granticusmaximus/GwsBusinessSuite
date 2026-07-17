using System.Collections.Concurrent;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GwsBusinessSuite.Infrastructure.Data;

public sealed class PublicContentCacheInvalidationInterceptor(
    IPublicContentCacheInvalidator invalidator) : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<DbContextId, byte> _dirtyContexts = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        MarkIfPublicContentChanged(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        MarkIfPublicContentChanged(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (TryTakeDirty(eventData.Context))
            invalidator.InvalidateAsync().AsTask().GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (TryTakeDirty(eventData.Context))
            await invalidator.InvalidateAsync(cancellationToken);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
        RemoveDirty(eventData.Context);

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        RemoveDirty(eventData.Context);
        return Task.CompletedTask;
    }

    private void MarkIfPublicContentChanged(DbContext? context)
    {
        if (context is null) return;

        var changed = context.ChangeTracker.Entries().Any(entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && entry.Entity is CmsSite or CmsPage or GlobalBlock or Article or ArticleCategory or SiteSettings);

        if (changed) _dirtyContexts.TryAdd(context.ContextId, 0);
    }

    private bool TryTakeDirty(DbContext? context) =>
        context is not null && _dirtyContexts.TryRemove(context.ContextId, out _);

    private void RemoveDirty(DbContext? context)
    {
        if (context is not null) _dirtyContexts.TryRemove(context.ContextId, out _);
    }
}
