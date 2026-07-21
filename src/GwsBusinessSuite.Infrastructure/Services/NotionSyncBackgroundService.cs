using GwsBusinessSuite.Application.Wiki;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NotionSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<NotionSyncBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public static readonly SemaphoreSlim SyncLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        await RunSyncAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            await RunSyncAsync(stoppingToken);
    }

    private async Task RunSyncAsync(CancellationToken cancellationToken)
    {
        if (!await SyncLock.WaitAsync(0, cancellationToken))
        {
            logger.LogInformation("Notion: scheduled sync skipped (a sync is already running)");
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var notionSync = scope.ServiceProvider.GetRequiredService<INotionSyncService>();
            var settings = await notionSync.GetSettingsAsync(cancellationToken);
            if (settings is null || !settings.AutoSyncEnabled
                || settings.IntegrationTokenUnreadable
                || string.IsNullOrWhiteSpace(settings.IntegrationToken))
            {
                logger.LogDebug("Notion: scheduled sync skipped because automatic sync is not configured and enabled");
                return;
            }

            var result = await notionSync.SyncAsync(cancellationToken);

            if (result.IsSuccess)
            {
                logger.LogInformation(
                    "Notion: scheduled sync complete ({Imported} imported, {Updated} updated, {Archived} archived) - {Message}",
                    result.Imported, result.Updated, result.Archived, result.Message);
            }
            else
            {
                logger.LogInformation("Notion: scheduled sync skipped or unsuccessful - {Message}", result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down — not an error.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Notion: scheduled sync failed");
        }
        finally
        {
            SyncLock.Release();
        }
    }
}
