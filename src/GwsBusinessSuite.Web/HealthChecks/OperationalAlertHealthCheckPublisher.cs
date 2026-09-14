using GwsBusinessSuite.Application.Operations;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GwsBusinessSuite.Web.HealthChecks;

// The five registered health checks (database, Ollama, backups, security-audit, disk-space)
// only ever ran when something happened to call /health - nobody was polling that, so a
// DiskSpaceHealthCheck.Unhealthy or an OllamaHealthCheck.Degraded could sit unnoticed
// indefinitely. IHealthCheckPublisher is ASP.NET Core's own periodic-evaluation hook (wired up
// by the framework's HealthCheckPublisherHostedService once HealthCheckPublisherOptions.Period
// is set - see Program.cs), so this doesn't need its own polling loop. IOperationalAlertService
// already throttles per-source, so a persistently unhealthy check pages once per cooldown
// window here too, not once per publish cycle.
public sealed class OperationalAlertHealthCheckPublisher(IOperationalAlertService alerts) : IHealthCheckPublisher
{
    public async Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        foreach (var (name, entry) in report.Entries)
        {
            if (entry.Status == HealthStatus.Healthy) continue;

            await alerts.NotifyFailureAsync(
                $"health-check-{name}",
                $"Health check '{name}' reported {entry.Status}: {entry.Description}",
                entry.Exception,
                cancellationToken);
        }
    }
}
