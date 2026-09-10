using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class WikiDatabaseRecurrenceService(
    IAppDbContext dbContext,
    IWikiDatabaseService wikiDatabaseService,
    TimeProvider timeProvider,
    ILogger<WikiDatabaseRecurrenceService>? logger = null) : IWikiDatabaseRecurrenceService
{
    public async Task<IReadOnlyList<WikiDatabaseRowRecurrenceView>> ListAsync(
        Guid wikiDatabaseId, CancellationToken cancellationToken = default)
    {
        var recurrences = await dbContext.WikiDatabaseRowRecurrences.AsNoTracking()
            .Where(item => item.WikiDatabaseId == wikiDatabaseId)
            .ToListAsync(cancellationToken);
        var templateNames = await dbContext.WikiDatabaseRowTemplates.AsNoTracking()
            .Where(item => item.WikiDatabaseId == wikiDatabaseId)
            .ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);

        return recurrences
            .Select(item => new WikiDatabaseRowRecurrenceView(
                item.Id,
                item.WikiDatabaseId,
                item.WikiDatabaseRowTemplateId,
                templateNames.GetValueOrDefault(item.WikiDatabaseRowTemplateId, "(deleted template)"),
                item.CronExpression,
                item.IsActive,
                item.NextRunAt,
                item.LastRunAt))
            .OrderBy(view => view.RowTemplateName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<WikiDatabaseRowRecurrenceView> CreateAsync(
        Guid wikiDatabaseId, Guid wikiDatabaseRowTemplateId, string cronExpression, string performedBy,
        CancellationToken cancellationToken = default)
    {
        var trimmedExpression = cronExpression?.Trim() ?? string.Empty;
        CronSchedule.Validate(trimmedExpression);

        var template = await dbContext.WikiDatabaseRowTemplates.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == wikiDatabaseRowTemplateId && item.WikiDatabaseId == wikiDatabaseId,
                cancellationToken)
            ?? throw new InvalidOperationException("The selected row template no longer exists in this database.");

        var now = timeProvider.GetUtcNow();
        var nextRunAt = CronSchedule.GetNextOccurrence(trimmedExpression, now);
        var recurrence = new WikiDatabaseRowRecurrence
        {
            WikiDatabaseId = wikiDatabaseId,
            WikiDatabaseRowTemplateId = wikiDatabaseRowTemplateId,
            CronExpression = trimmedExpression,
            NextRunAt = nextRunAt,
            NextRunAtUnixSeconds = nextRunAt.ToUnixTimeSeconds(),
            CreatedAt = now,
            CreatedBy = performedBy
        };
        dbContext.WikiDatabaseRowRecurrences.Add(recurrence);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new WikiDatabaseRowRecurrenceView(
            recurrence.Id, wikiDatabaseId, wikiDatabaseRowTemplateId, template.Name,
            trimmedExpression, true, nextRunAt, null);
    }

    public async Task SetActiveAsync(
        Guid recurrenceId, bool isActive, string performedBy, CancellationToken cancellationToken = default)
    {
        var recurrence = await dbContext.WikiDatabaseRowRecurrences
            .FirstOrDefaultAsync(item => item.Id == recurrenceId, cancellationToken)
            ?? throw new InvalidOperationException("The recurrence no longer exists.");

        if (recurrence.IsActive == isActive)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        recurrence.IsActive = isActive;
        recurrence.UpdatedAt = now;
        recurrence.UpdatedBy = performedBy;
        if (isActive)
        {
            // Re-enabling recomputes from "now" rather than resuming a stale NextRunAt that may
            // be long past - the same choice AutomationWorkflowService makes when a schedule
            // trigger node's parameters change on publish, not "catch up" on every missed tick.
            var nextRunAt = CronSchedule.GetNextOccurrence(recurrence.CronExpression, now);
            recurrence.NextRunAt = nextRunAt;
            recurrence.NextRunAtUnixSeconds = nextRunAt.ToUnixTimeSeconds();
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid recurrenceId, CancellationToken cancellationToken = default)
    {
        var recurrence = await dbContext.WikiDatabaseRowRecurrences
            .FirstOrDefaultAsync(item => item.Id == recurrenceId, cancellationToken);
        if (recurrence is null)
        {
            return;
        }
        dbContext.WikiDatabaseRowRecurrences.Remove(recurrence);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var nowUnix = now.ToUnixTimeSeconds();
        // Filtered on the long column, never NextRunAt itself - SQLite can't translate a
        // DateTimeOffset range comparison (see WikiDatabaseRowRecurrence's own field comment).
        var due = await dbContext.WikiDatabaseRowRecurrences
            .Where(item => item.IsActive
                && item.NextRunAtUnixSeconds != null
                && item.NextRunAtUnixSeconds <= nowUnix)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        // Advance every due recurrence's NextRunAt and save before materializing anything - if
        // a row creation below throws or the process crashes mid-sweep, no recurrence is left
        // pointing at a past NextRunAt that the very next tick would fire again.
        foreach (var recurrence in due)
        {
            var nextRunAt = CronSchedule.GetNextOccurrence(recurrence.CronExpression, now);
            recurrence.NextRunAt = nextRunAt;
            recurrence.NextRunAtUnixSeconds = nextRunAt.ToUnixTimeSeconds();
            recurrence.LastRunAt = now;
            recurrence.UpdatedAt = now;
            recurrence.UpdatedBy = "recurrence-scheduler";
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        var created = 0;
        foreach (var recurrence in due)
        {
            try
            {
                await wikiDatabaseService.CreateRowFromTemplateAsync(
                    recurrence.WikiDatabaseId, recurrence.WikiDatabaseRowTemplateId, parentRowId: null,
                    performedBy: "recurrence-scheduler", cancellationToken);
                created++;
            }
            catch (Exception ex)
            {
                // A single recurrence's own failure (its source template deleted, the database
                // itself trashed) must never stop every other due recurrence in the same sweep
                // from still firing - same non-blocking contract as
                // AutomationTriggerService.TriggerDatabaseRowChangedAsync's per-subscriber loop.
                logger?.LogWarning(ex, "Recurring row creation failed for recurrence {RecurrenceId}.", recurrence.Id);
            }
        }
        return created;
    }
}
