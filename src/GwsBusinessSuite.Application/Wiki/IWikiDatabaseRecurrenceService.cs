using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// A recurring row is "materialize this row template again on a schedule" - see
// WikiDatabaseRowRecurrence's own comment for why it points at a template rather than a live
// row. Sits beside IWikiDatabaseService rather than as members on it: the row-template CRUD it
// depends on already exists there, and this is a genuinely separate concern (scheduling) with
// its own background sweep, matching how IAutomationTriggerService is its own interface next to
// IAutomationWorkflowService rather than folded into it.
public sealed record WikiDatabaseRowRecurrenceView(
    Guid Id,
    Guid WikiDatabaseId,
    Guid WikiDatabaseRowTemplateId,
    string RowTemplateName,
    string CronExpression,
    bool IsActive,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt);

public interface IWikiDatabaseRecurrenceService
{
    Task<IReadOnlyList<WikiDatabaseRowRecurrenceView>> ListAsync(
        Guid wikiDatabaseId, CancellationToken cancellationToken = default);

    // Throws FormatException (via CronSchedule.Validate) for a malformed expression - the same
    // exception type/message core.scheduleTrigger's own cron field already surfaces, so a
    // caller doesn't need a second error-shape to handle.
    Task<WikiDatabaseRowRecurrenceView> CreateAsync(
        Guid wikiDatabaseId, Guid wikiDatabaseRowTemplateId, string cronExpression, string performedBy,
        CancellationToken cancellationToken = default);

    Task SetActiveAsync(Guid recurrenceId, bool isActive, string performedBy, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid recurrenceId, CancellationToken cancellationToken = default);

    // Materializes every due, active recurrence via IWikiDatabaseService.CreateRowFromTemplateAsync
    // and advances each one's NextRunAt before creating anything, mirroring
    // IAutomationTriggerService.RunDueSchedulesAsync's own recompute-then-act order so a slow or
    // crashing materialization can never cause the same recurrence to fire twice for one
    // occurrence. Returns how many rows were created. Never throws for one recurrence's own
    // failure (its source template deleted out from under it, etc.) - logged and skipped rather
    // than blocking every other recurrence's sweep.
    Task<int> RunDueAsync(CancellationToken cancellationToken = default);
}
