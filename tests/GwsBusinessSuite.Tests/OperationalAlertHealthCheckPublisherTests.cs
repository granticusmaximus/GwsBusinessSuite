using FluentAssertions;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Web.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GwsBusinessSuite.Tests;

public sealed class OperationalAlertHealthCheckPublisherTests
{
    [Fact]
    public async Task PublishAsync_ShouldAlertOnEveryNonHealthyEntry()
    {
        // Regression guard for a real gap: the five registered health checks (database, Ollama,
        // backups, security-audit, disk-space) only ever ran when something hit /health, which
        // nothing did proactively - an Unhealthy disk-space or Degraded Ollama result could sit
        // unnoticed indefinitely. This publisher is what Program.cs's HealthCheckPublisherOptions
        // wiring hands every periodic report to.
        var alerts = new RecordingAlertService();
        var publisher = new OperationalAlertHealthCheckPublisher(alerts);
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["database"] = new(HealthStatus.Healthy, "Connected.", TimeSpan.Zero, null, null),
                ["disk-space"] = new(HealthStatus.Unhealthy, "412.3 MB free.", TimeSpan.Zero, null, null),
                ["ollama"] = new(HealthStatus.Degraded, "Zero models installed.", TimeSpan.Zero, null, null)
            },
            TimeSpan.FromMilliseconds(50));

        await publisher.PublishAsync(report, CancellationToken.None);

        alerts.Calls.Should().HaveCount(2);
        alerts.Calls.Should().Contain(c => c.Source == "health-check-disk-space" && c.Summary.Contains("Unhealthy") && c.Summary.Contains("412.3 MB free"));
        alerts.Calls.Should().Contain(c => c.Source == "health-check-ollama" && c.Summary.Contains("Degraded"));
        alerts.Calls.Should().NotContain(c => c.Source == "health-check-database");
    }

    [Fact]
    public async Task PublishAsync_ShouldPassTheHealthCheckExceptionThrough()
    {
        var alerts = new RecordingAlertService();
        var publisher = new OperationalAlertHealthCheckPublisher(alerts);
        var thrown = new InvalidOperationException("disk space could not be determined");
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["disk-space"] = new(HealthStatus.Unhealthy, "Disk space could not be determined.", TimeSpan.Zero, thrown, null)
            },
            TimeSpan.FromMilliseconds(10));

        await publisher.PublishAsync(report, CancellationToken.None);

        alerts.Calls.Should().ContainSingle(c => c.Exception == thrown);
    }

    private sealed class RecordingAlertService : IOperationalAlertService
    {
        public List<(string Source, string Summary, Exception? Exception)> Calls { get; } = [];

        public Task NotifyFailureAsync(string source, string summary, Exception? exception = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((source, summary, exception));
            return Task.CompletedTask;
        }
    }
}
