using System.Runtime.CompilerServices;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GwsBusinessSuite.Infrastructure.Data;

// Boards K6, live updates: notes which Sentinel databases a save touches (rows, properties, board
// settings) and tells WikiDatabaseChangeNotifier once the save has gone through. One save hook
// covers every writer - the board, Sentinel's views, workflows, recurring rows - without
// instrumenting each one. A failed save publishes nothing.
public sealed class WikiDatabaseChangeInterceptor(WikiDatabaseChangeNotifier notifier) : SaveChangesInterceptor
{
    private sealed record Pending(Guid DatabaseId, Guid? RowId, Guid? ParentRowId, string? Actor);

    private readonly ConditionalWeakTable<DbContext, List<Pending>> _pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context) Collect(context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) Collect(context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context) Flush(context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) Flush(context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context) _pending.Remove(context);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) _pending.Remove(context);
        return Task.CompletedTask;
    }

    private void Collect(DbContext context)
    {
        var changed = new List<Pending>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            switch (entry.Entity)
            {
                case WikiDatabaseRow row:
                    // A row only re-sorted (its column took a card) isn't a change to it.
                    var reordered = entry.State == EntityState.Modified && entry.Properties
                        .Where(property => property.IsModified)
                        .All(property => property.Metadata.Name is nameof(WikiDatabaseRow.SortOrder) or nameof(WikiDatabaseRow.UpdatedAt) or nameof(WikiDatabaseRow.UpdatedBy));
                    changed.Add(new Pending(row.WikiDatabaseId, reordered ? null : row.Id, reordered ? null : row.ParentRowId, row.UpdatedBy));
                    break;
                case WikiDatabaseProperty property:
                    changed.Add(new Pending(property.WikiDatabaseId, null, null, property.UpdatedBy));
                    break;
                case KanbanBoardSettings settings:
                    changed.Add(new Pending(settings.WikiDatabaseId, null, null, settings.UpdatedBy));
                    break;
            }
        }
        if (changed.Count == 0) return;
        _pending.AddOrUpdate(context, _pending.TryGetValue(context, out var earlier) ? [.. earlier, .. changed] : changed);
    }

    private void Flush(DbContext context)
    {
        if (!_pending.TryGetValue(context, out var changed)) return;
        _pending.Remove(context);
        foreach (var database in changed.GroupBy(item => item.DatabaseId))
        {
            var actor = database.Select(item => item.Actor).LastOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "someone";
            notifier.Publish(
                database.Key,
                database.Select(item => item.RowId).OfType<Guid>().Distinct().ToList(),
                database.Select(item => item.ParentRowId).OfType<Guid>().Distinct().ToList(),
                actor,
                context);
        }
    }
}
