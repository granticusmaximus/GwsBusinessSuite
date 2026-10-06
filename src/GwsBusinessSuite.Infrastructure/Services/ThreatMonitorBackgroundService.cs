using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Runs the "My exposure" checks on a schedule and sends the daily threat digest. Does nothing
// until someone has opened My exposure (which creates the ThreatMonitorSettings row), so a fresh
// install - and the test host - never makes these outside calls on its own.
public sealed class ThreatMonitorBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOperationalAlertService alerts,
    TimeProvider timeProvider,
    ILogger<ThreatMonitorBackgroundService> logger) : BackgroundService
{
    public static readonly TimeSpan AssetInterval = TimeSpan.FromHours(12);
    public static readonly TimeSpan StackInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan DependencyInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), timeProvider, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Threat monitor run failed.");
                await alerts.NotifyFailureAsync("threat-monitor", "The Threat Intelligence exposure monitor failed.", ex, stoppingToken);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var settings = await db.ThreatMonitorSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null) return;

        var now = timeProvider.GetUtcNow();
        var scopeToRun = DueChecks(settings.LastAssetCheckAt, settings.LastStackCheckAt, settings.LastDependencyCheckAt, now);
        var monitor = scope.ServiceProvider.GetRequiredService<IThreatMonitorService>();
        if (scopeToRun != ThreatCheckScope.None)
        {
            var result = await monitor.RunChecksAsync(scopeToRun, ct);
            logger.LogInformation("Threat monitor ran {Scope}: {New} new, {Resolved} resolved finding(s).", scopeToRun, result.NewFindings, result.ResolvedFindings);
        }

        if (DigestDue(settings.DigestEnabled, settings.DigestHourLocal, settings.LastDigestSentAt, now.ToLocalTime()))
        {
            var sent = await monitor.SendDigestAsync(force: false, ct);
            logger.LogInformation("Threat digest: {Message}", sent.Message);
        }
    }

    public static ThreatCheckScope DueChecks(DateTimeOffset? lastAssets, DateTimeOffset? lastStack, DateTimeOffset? lastDependencies, DateTimeOffset now)
    {
        var scope = ThreatCheckScope.None;
        if (lastAssets is null || now - lastAssets >= AssetInterval) scope |= ThreatCheckScope.Assets;
        if (lastStack is null || now - lastStack >= StackInterval) scope |= ThreatCheckScope.Stack;
        if (lastDependencies is null || now - lastDependencies >= DependencyInterval) scope |= ThreatCheckScope.Dependencies;
        return scope;
    }

    // Once a day, at or after the chosen local hour.
    public static bool DigestDue(bool enabled, int hourLocal, DateTimeOffset? lastSent, DateTimeOffset nowLocal)
    {
        if (!enabled || nowLocal.Hour < hourLocal) return false;
        return lastSent is null || lastSent.Value.ToOffset(nowLocal.Offset).Date < nowLocal.Date;
    }
}
