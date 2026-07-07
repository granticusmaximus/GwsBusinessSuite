using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Mirrors NewsRefreshBackgroundService's PeriodicTimer + scoped-service-resolution
// pattern. Keeps an in-memory "was this container already in an error state" map so
// an alert is only raised on the *transition* into error, not on every single poll
// while it stays broken.
public sealed class DockerHealthMonitorBackgroundService(
    IServiceScopeFactory scopeFactory,
    DockerHealthNotifier notifier,
    ILogger<DockerHealthMonitorBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, bool> _lastKnownError = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await PollAsync(stoppingToken);
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDockerHealthService>();
            var result = await service.ListContainersAsync(ct);

            if (!result.Available)
            {
                // No socket mounted (e.g. local dev) - nothing to monitor, not an error.
                return;
            }

            foreach (var container in result.Containers)
            {
                var wasError = _lastKnownError.GetValueOrDefault(container.Name);
                _lastKnownError[container.Name] = container.IsError;

                if (container.IsError && !wasError)
                {
                    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>();
                    await RaiseAlertAsync(dbContextFactory, container, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down — not an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Docker health poll failed");
        }
    }

    private async Task RaiseAlertAsync(IAppDbContextFactory dbContextFactory, ContainerHealthView container, CancellationToken ct)
    {
        var message = BuildAlertMessage(container);
        logger.LogWarning("Docker health: {Container} entered an error state - {Message}", container.Name, message);

        var alert = new DockerHealthAlert
        {
            ContainerName = container.Name,
            Severity = DockerHealthAlertSeverity.Error,
            Message = message,
            CreatedBy = "docker-health-monitor"
        };

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        await db.DockerHealthAlerts.AddAsync(alert, ct);
        await db.SaveChangesAsync(ct);

        notifier.Publish(new DockerHealthAlertView
        {
            Id = alert.Id,
            ContainerName = alert.ContainerName,
            Severity = alert.Severity,
            Message = alert.Message,
            IsRead = false,
            CreatedAt = alert.CreatedAt
        });
    }

    private static string BuildAlertMessage(ContainerHealthView container)
    {
        if (container.ExitCode == 137) return "Exited with code 137 (out of memory)";
        if (string.Equals(container.Health, "unhealthy", StringComparison.OrdinalIgnoreCase)) return "Healthcheck reports unhealthy";
        if (container.RestartCount >= 5) return $"Restarted {container.RestartCount} times (crash loop)";
        if (container.ExitCode != 0) return $"Exited with code {container.ExitCode}";
        return $"Entered state '{container.State}'";
    }
}
