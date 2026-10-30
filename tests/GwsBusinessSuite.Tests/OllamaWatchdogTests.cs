using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

// The watchdog that restarts a wedged Ollama container (30-10-26).
public sealed class OllamaWatchdogTests
{
    [Fact]
    public async Task Check_ShouldRestartOllama_AfterFourUnansweredChecks_ThenWaitOutTheCooldown()
    {
        var (watchdog, docker, ollama, clock, _) = Create();
        ollama.Answers = false;

        for (var i = 1; i < OllamaWatchdogBackgroundService.FailuresBeforeRestart; i++)
            (await watchdog.CheckAsync(CancellationToken.None)).Should().BeFalse($"only {i} failed so far");
        (await watchdog.CheckAsync(CancellationToken.None)).Should().BeTrue();
        docker.Restarted.Should().Equal("gwssuite-ollama-1");

        clock.Advance(TimeSpan.FromMinutes(2));
        for (var i = 0; i < OllamaWatchdogBackgroundService.FailuresBeforeRestart; i++) await watchdog.CheckAsync(CancellationToken.None);
        docker.Restarted.Should().HaveCount(1, "restarts are at least 15 minutes apart");

        ollama.Answers = true;
        (await watchdog.CheckAsync(CancellationToken.None)).Should().BeFalse("it answers again");
    }

    [Fact]
    public async Task Check_ShouldRestartOllama_WhenOneJobHoldsTheModelPastItsLimit()
    {
        var (watchdog, docker, _, clock, workloads) = Create(timeoutMinutes: 30);
        await using var held = await workloads.AcquireAsync(OllamaWorkloadPriority.Background);

        clock.Advance(TimeSpan.FromMinutes(35));
        (await watchdog.CheckAsync(CancellationToken.None)).Should().BeFalse("30 minutes is allowed, and 10 more grace");
        clock.Advance(TimeSpan.FromMinutes(6));
        (await watchdog.CheckAsync(CancellationToken.None)).Should().BeTrue();
        docker.Restarted.Should().ContainSingle();
    }

    [Fact]
    public async Task Check_ShouldLeaveThingsAlone_WhenThereIsNoOllamaContainer()
    {
        var (watchdog, docker, ollama, _, _) = Create(withContainer: false);
        ollama.Answers = false;
        for (var i = 0; i < 6; i++) (await watchdog.CheckAsync(CancellationToken.None)).Should().BeFalse();
        docker.Restarted.Should().BeEmpty();
    }

    private static (OllamaWatchdogBackgroundService Watchdog, FakeDocker Docker, FakeOllama Ollama, ManualClock Clock, OllamaWorkloadScheduler Workloads)
        Create(bool withContainer = true, int? timeoutMinutes = null)
    {
        var docker = new FakeDocker(withContainer);
        var ollama = new FakeOllama();
        var clock = new ManualClock();
        var workloads = new OllamaWorkloadScheduler(clock);
        var services = new ServiceCollection()
            .AddSingleton<IDockerHealthService>(docker)
            .AddSingleton<ISiteSettingsService>(new FakeSettings(timeoutMinutes))
            .BuildServiceProvider();
        var watchdog = new OllamaWatchdogBackgroundService(services.GetRequiredService<IServiceScopeFactory>(), new FakeHttp(ollama), workloads,
            Options.Create(new ContentStudioOptions { BaseUrl = "http://ollama.test:11434" }), new ConfigurationBuilder().Build(), clock,
            NullLogger<OllamaWatchdogBackgroundService>.Instance);
        return (watchdog, docker, ollama, clock, workloads);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeOllama : HttpMessageHandler
    {
        public bool Answers { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Answers ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)) : throw new HttpRequestException("connection refused");
    }

    private sealed class FakeHttp(FakeOllama handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeSettings(int? timeoutMinutes) : ISiteSettingsService
    {
        public Task<SiteSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteSettingsView(10, null, null, null, timeoutMinutes, null, 10));
        public Task SaveSettingsAsync(SiteSettingsView settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeDocker(bool withContainer) : IDockerHealthService
    {
        public List<string> Restarted { get; } = [];

        public Task<ContainerListResult> ListContainersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContainerListResult(true, withContainer
                ? [new ContainerHealthView { Name = "/gwssuite-ollama-1", Image = "ollama/ollama:latest", State = "running" },
                   new ContainerHealthView { Name = "/gwssuite-gwssuite-1", Image = "gwssuite-web:abc", State = "running" }]
                : [new ContainerHealthView { Name = "/gwssuite-gwssuite-1", Image = "gwssuite-web:abc", State = "running" }]));

        public Task<DockerActionResult> RestartContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default)
        {
            Restarted.Add(containerName);
            return Task.FromResult(new DockerActionResult(true, "Container restarted."));
        }

        public Task<ContainerDetailView?> GetContainerDetailsAsync(string containerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DockerHealthAlertView>> ListAlertsAsync(bool unreadOnly, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkAlertReadAsync(Guid alertId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkAllAlertsReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CountUnreadAlertsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DockerActionResult> StartContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DockerActionResult> StopContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DockerActionResult> RemoveContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DockerActionResult> PullImageAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DockerActionResult> RecreateContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DockerActionResult> ExecCommandAsync(string containerName, string command, string performedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DockerActionLogView>> ListActionLogsAsync(string? containerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
