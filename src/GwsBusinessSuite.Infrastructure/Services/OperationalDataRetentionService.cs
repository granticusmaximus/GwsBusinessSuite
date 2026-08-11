using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class OperationalDataRetentionOptions
{
    public const string SectionName = "OperationalRetention";

    public int AutomationExecutionDays { get; set; } = 90;
    public int SocialPostAlertDays { get; set; } = 90;
    public int LiveShowRecordingDays { get; set; } = 180;
    public int AppGenerationMessageDays { get; set; } = 180;
    public int NewsItemDays { get; set; } = 30;
    // Financial/revenue history, not an operational log - kept far longer than the others by
    // default. CjCommissionRecord's row volume is naturally low (one row per commission
    // event, not per request/tick), so the "unbounded growth" concern that applies to the
    // other tables here is much smaller in practice; this exists mainly as a long-horizon
    // safety net, not an active pruning need.
    public int CjCommissionRecordDays { get; set; } = 1095;
    public int PodcastListenProgressDays { get; set; } = 365;
}

// Runs daily, same cadence as PrivacyRetentionPurgeBackgroundService - a purge sweep only
// needs day-level precision and each pass can touch a meaningful number of rows.
public sealed class OperationalDataRetentionService(
    IAppDbContext db,
    IOptions<OperationalDataRetentionOptions> options,
    IConfiguration configuration,
    ILogger<OperationalDataRetentionService> logger) : IOperationalDataRetentionService
{
    private readonly OperationalDataRetentionOptions _options = options.Value;

    public async Task<int> PurgeExpiredRecordsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var total = 0;
        total += await PurgeAutomationExecutionsAsync(now.AddDays(-Math.Max(1, _options.AutomationExecutionDays)), cancellationToken);
        total += await PurgeByCreatedAtAsync(db.SocialPostAlerts, now.AddDays(-Math.Max(1, _options.SocialPostAlertDays)), cancellationToken);
        total += await PurgeLiveShowRecordingsAsync(now.AddDays(-Math.Max(1, _options.LiveShowRecordingDays)), cancellationToken);
        total += await PurgeByCreatedAtAsync(db.AppGenerationMessages, now.AddDays(-Math.Max(1, _options.AppGenerationMessageDays)), cancellationToken);
        total += await PurgeNewsItemsAsync(now.AddDays(-Math.Max(1, _options.NewsItemDays)), cancellationToken);
        total += await PurgeCjCommissionRecordsAsync(now.AddDays(-Math.Max(1, _options.CjCommissionRecordDays)), cancellationToken);
        total += await PurgeByLastPlayedAtAsync(now.AddDays(-Math.Max(1, _options.PodcastListenProgressDays)), cancellationToken);

        if (total > 0) logger.LogInformation("Operational data retention purge deleted {Count} expired record(s).", total);
        return total;
    }

    // Only terminal-status executions (Succeeded/Failed/Canceled) are eligible - a Waiting or
    // Running execution must never be purged regardless of age, and FinishedAtUnixSeconds is
    // only ever set once an execution reaches a terminal state anyway.
    private async Task<int> PurgeAutomationExecutionsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var cutoffUnix = cutoff.ToUnixTimeSeconds();
        var terminalStatuses = new[]
        {
            AutomationExecutionStatuses.Succeeded, AutomationExecutionStatuses.Failed, AutomationExecutionStatuses.Canceled
        };
        var expiredIds = await db.AutomationExecutions
            .Where(x => terminalStatuses.Contains(x.Status) && x.FinishedAtUnixSeconds != null && x.FinishedAtUnixSeconds < cutoffUnix)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (expiredIds.Count == 0) return 0;

        // Delete children first rather than relying on the DB's ON DELETE CASCADE - SQLite
        // only enforces FK cascades when "PRAGMA foreign_keys = ON" is set for the connection,
        // which this app doesn't guarantee is always the case, so this stays correct either way.
        var childExecutions = await db.AutomationNodeExecutions
            .Where(x => expiredIds.Contains(x.ExecutionId))
            .ToListAsync(cancellationToken);
        db.AutomationNodeExecutions.RemoveRange(childExecutions);

        var expiredExecutions = await db.AutomationExecutions.Where(x => expiredIds.Contains(x.Id)).ToListAsync(cancellationToken);
        db.AutomationExecutions.RemoveRange(expiredExecutions);
        await db.SaveChangesAsync(cancellationToken);
        return expiredExecutions.Count;
    }

    private async Task<int> PurgeLiveShowRecordingsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var candidates = await db.LiveShowRecordings.AsNoTracking()
            .Select(x => new { x.Id, x.CreatedAt, x.FileName })
            .ToListAsync(cancellationToken);
        var expired = candidates.Where(x => x.CreatedAt < cutoff).ToList();
        if (expired.Count == 0) return 0;

        var recordingsRootPath = configuration["LiveShow:RecordingsPath"] ?? "/app/data/live-show-recordings";
        foreach (var recording in expired)
        {
            var filePath = System.IO.Path.Combine(recordingsRootPath, recording.FileName);
            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
            }
            catch (IOException ex)
            {
                // A file that can't be deleted this sweep (locked, permissions) just gets
                // retried next sweep - not worth failing the whole purge over one recording.
                logger.LogWarning(ex, "Could not delete expired live show recording file {FilePath}.", filePath);
            }
        }

        var expiredIds = expired.Select(x => x.Id).ToHashSet();
        var rows = await db.LiveShowRecordings.Where(x => expiredIds.Contains(x.Id)).ToListAsync(cancellationToken);
        db.LiveShowRecordings.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private async Task<int> PurgeNewsItemsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var cutoffUnix = cutoff.ToUnixTimeSeconds();
        var expired = await db.NewsItems.Where(x => x.FetchedAtUnixSeconds < cutoffUnix).ToListAsync(cancellationToken);
        if (expired.Count == 0) return 0;
        db.NewsItems.RemoveRange(expired);
        await db.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }

    private async Task<int> PurgeCjCommissionRecordsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var cutoffUnix = cutoff.ToUnixTimeSeconds();
        var expired = await db.CjCommissionRecords.Where(x => x.CreatedAtUnixSeconds < cutoffUnix).ToListAsync(cancellationToken);
        if (expired.Count == 0) return 0;
        db.CjCommissionRecords.RemoveRange(expired);
        await db.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }

    // No Unix-seconds shadow column on LastPlayedAt - same materialize-then-filter fallback
    // as PrivacyOperationsService.PurgeByCreatedAtAsync, adapted for this table's own
    // "recency" column.
    private async Task<int> PurgeByLastPlayedAtAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var candidates = await db.PodcastListenProgresses.AsNoTracking()
            .Select(x => new { x.Id, x.LastPlayedAt })
            .ToListAsync(cancellationToken);
        var expiredIds = candidates.Where(x => x.LastPlayedAt < cutoff).Select(x => x.Id).ToHashSet();
        if (expiredIds.Count == 0) return 0;

        var expiredRows = await db.PodcastListenProgresses.Where(x => expiredIds.Contains(x.Id)).ToListAsync(cancellationToken);
        db.PodcastListenProgresses.RemoveRange(expiredRows);
        await db.SaveChangesAsync(cancellationToken);
        return expiredRows.Count;
    }

    // SQLite/EF Core can't translate a server-side range filter on a DateTimeOffset column -
    // same pattern as PrivacyOperationsService.PurgeByCreatedAtAsync (materialize CreatedAt to
    // filter client-side, then delete only the matching rows by Id).
    private async Task<int> PurgeByCreatedAtAsync<T>(
        DbSet<T> set, DateTimeOffset cutoff, CancellationToken cancellationToken)
        where T : GwsBusinessSuite.Domain.Common.AuditableEntity
    {
        var candidates = await set.AsNoTracking().Select(x => new { x.Id, x.CreatedAt }).ToListAsync(cancellationToken);
        var expiredIds = candidates.Where(x => x.CreatedAt < cutoff).Select(x => x.Id).ToHashSet();
        if (expiredIds.Count == 0) return 0;

        var expiredRows = await set.Where(x => expiredIds.Contains(x.Id)).ToListAsync(cancellationToken);
        set.RemoveRange(expiredRows);
        await db.SaveChangesAsync(cancellationToken);
        return expiredRows.Count;
    }
}
