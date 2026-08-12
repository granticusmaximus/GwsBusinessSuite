using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Part 6.5 (scheduled publishing). The publish-time visibility gate itself
// (PublicationWindows.IsVisible) already worked before this pass and needs no sweep - a page
// scheduled for a future PublishedAt is already correctly invisible until that instant, on every
// request, with no job involved. This sweep exists for one narrower gap: automations reacting to
// cms.pagePublishedTrigger need to fire at the real publish moment, not at the moment someone
// scheduled it - see CmsBuilderService.SavePageAsync's ScheduledPublishTriggerPending deferral.
public sealed class CmsScheduledPublishBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOperationalAlertService alerts,
    ILogger<CmsScheduledPublishBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var cmsBuilderService = scope.ServiceProvider.GetRequiredService<ICmsBuilderService>();
                var count = await cmsBuilderService.RunScheduledPublishSweepAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Fired the publish automation trigger for {Count} scheduled CMS page(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "CMS scheduled-publish sweep failed.");
                await alerts.NotifyFailureAsync("cms-scheduled-publish-sweep", "The CMS scheduled-publish automation sweep failed.", ex, stoppingToken);
            }
        }
    }
}
