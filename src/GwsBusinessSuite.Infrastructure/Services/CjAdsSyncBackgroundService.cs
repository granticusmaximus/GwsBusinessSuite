using GwsBusinessSuite.Application.AffiliateSuggestions;
using GwsBusinessSuite.Application.CjAds;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Mirrors NewsRefreshBackgroundService's PeriodicTimer + scoped-service-resolution
// pattern. Before this, CJ link/offer sync and affiliate-suggestion generation were
// manual-only (a button in CjAds.razor / AffiliateSuggestions.razor) - expired promos
// and newly published articles with no suggestions at all just sat there until someone
// clicked. SyncAllLinksAsync is a safe no-op when no CJ connector is configured yet
// (it loops over whatever's in the partner roster, which is empty in that case).
public sealed class CjAdsSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<CjAdsSyncBackgroundService> logger) : BackgroundService
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

    private async Task RunSyncAsync(CancellationToken ct)
    {
        if (!await SyncLock.WaitAsync(0, ct))
        {
            logger.LogInformation("CJ Ads: scheduled sync skipped (a sync is already running)");
            return;
        }
        try
        {
            logger.LogInformation("CJ Ads: starting scheduled link sync");
            await using var scope = scopeFactory.CreateAsyncScope();
            var cjAds = scope.ServiceProvider.GetRequiredService<ICjAdsService>();
            var linkResult = await cjAds.SyncAllLinksAsync(ct);
            logger.LogInformation(
                "CJ Ads: link sync complete ({Processed}/{Total} advertisers, {Imported} links imported)",
                linkResult.AdvertisersProcessed, linkResult.AdvertisersProcessed + linkResult.AdvertisersFailed, linkResult.TotalLinksImported);

            var suggestions = scope.ServiceProvider.GetRequiredService<IAffiliateSuggestionService>();
            var suggestionResult = await suggestions.GenerateForUnmatchedArticlesAsync(ct);
            logger.LogInformation(
                "CJ Ads: affiliate suggestion sweep complete ({Processed} article(s), {Created} suggestion(s) created)",
                suggestionResult.ArticlesProcessed, suggestionResult.SuggestionsCreated);
        }
        catch (OperationCanceledException)
        {
            // Shutting down — not an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CJ Ads: scheduled sync failed");
        }
        finally
        {
            SyncLock.Release();
        }
    }
}
