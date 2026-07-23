using GwsBusinessSuite.Application.Wiki;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NotionSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<NotionSyncBackgroundService> logger) : BackgroundService, INotionSyncCoordinator
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly Channel<bool> _manualRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private readonly object _statusLock = new();
    private NotionSyncJobStatus _status = NotionSyncJobStatus.Idle;

    private static readonly SemaphoreSlim SyncLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var manualLoop = RunManualLoopAsync(stoppingToken);
        var scheduledLoop = RunScheduledLoopAsync(stoppingToken);
        await Task.WhenAll(manualLoop, scheduledLoop);
    }

    public bool TryQueueManualSync()
    {
        lock (_statusLock)
        {
            if (_status.IsActive || !_manualRequests.Writer.TryWrite(true))
            {
                return false;
            }

            _status = new NotionSyncJobStatus(
                NotionSyncJobStates.Queued,
                "manual",
                null,
                null,
                null);
            return true;
        }
    }

    public NotionSyncJobStatus GetStatus()
    {
        lock (_statusLock)
        {
            return _status;
        }
    }

    private async Task RunManualLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _manualRequests.Reader.ReadAllAsync(stoppingToken))
        {
            await RunSyncAsync("manual", requireAutomaticSync: false, waitForLock: true, stoppingToken);
        }
    }

    private async Task RunScheduledLoopAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        await RunSyncAsync("scheduled", requireAutomaticSync: true, waitForLock: false, stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSyncAsync("scheduled", requireAutomaticSync: true, waitForLock: false, stoppingToken);
        }
    }

    private async Task RunSyncAsync(
        string source,
        bool requireAutomaticSync,
        bool waitForLock,
        CancellationToken cancellationToken)
    {
        var acquired = waitForLock
            ? await SyncLock.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken)
            : await SyncLock.WaitAsync(0, cancellationToken);
        if (!acquired)
        {
            logger.LogInformation("Notion: scheduled sync skipped (a sync is already running)");
            return;
        }

        try
        {
            if (source == "manual")
            {
                SetStatus(new NotionSyncJobStatus(
                    NotionSyncJobStates.Running,
                    source,
                    DateTimeOffset.UtcNow,
                    null,
                    null));
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var notionSync = scope.ServiceProvider.GetRequiredService<INotionSyncService>();
            var settings = await notionSync.GetSettingsAsync(cancellationToken);
            if (settings is null || (requireAutomaticSync && !settings.AutoSyncEnabled)
                || settings.IntegrationTokenUnreadable
                || !settings.HasStoredIntegrationToken)
            {
                var unavailable = new NotionSyncResult(
                    false,
                    requireAutomaticSync
                        ? "Automatic Notion sync is not configured and enabled."
                        : "No readable Notion integration token is configured.",
                    0,
                    0,
                    0);
                if (source == "manual")
                {
                    CompleteStatus(source, unavailable);
                }
                logger.LogDebug("Notion: {Source} sync skipped because the connector is not configured and enabled", source);
                return;
            }

            var result = await notionSync.SyncAsync(cancellationToken);
            if (source == "manual")
            {
                CompleteStatus(source, result);
            }

            if (result.IsSuccess)
            {
                logger.LogInformation(
                    "Notion: {Source} sync complete ({Imported} imported, {Updated} updated, {Archived} archived) - {Message}",
                    source, result.Imported, result.Updated, result.Archived, result.Message);
            }
            else
            {
                logger.LogInformation("Notion: {Source} sync skipped or unsuccessful - {Message}", source, result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down — not an error.
        }
        catch (Exception ex)
        {
            var result = new NotionSyncResult(false, $"Sync failed: {ex.GetBaseException().Message}", 0, 0, 0);
            if (source == "manual")
            {
                CompleteStatus(source, result);
            }
            logger.LogError(ex, "Notion: {Source} sync failed", source);
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private void CompleteStatus(string source, NotionSyncResult result)
    {
        lock (_statusLock)
        {
            _status = new NotionSyncJobStatus(
                NotionSyncJobStates.Completed,
                source,
                _status.Source == source ? _status.StartedAt : null,
                DateTimeOffset.UtcNow,
                result);
        }
    }

    private void SetStatus(NotionSyncJobStatus status)
    {
        lock (_statusLock)
        {
            _status = status;
        }
    }
}
