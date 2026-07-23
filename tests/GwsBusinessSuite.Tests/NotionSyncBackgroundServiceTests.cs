using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NotionSyncBackgroundServiceTests
{
    [Fact]
    public async Task ManualSync_ShouldRunOutsideTheCallerScopeAndPublishCompletion()
    {
        var notionSync = new FakeNotionSyncService();
        var services = new ServiceCollection();
        services.AddScoped<INotionSyncService>(_ => notionSync);
        await using var provider = services.BuildServiceProvider();
        var worker = new NotionSyncBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NotionSyncBackgroundService>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            worker.TryQueueManualSync().Should().BeTrue();
            await notionSync.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            worker.GetStatus().State.Should().Be(NotionSyncJobStates.Running);
            worker.TryQueueManualSync().Should().BeFalse("only one workspace sync may run at a time");

            notionSync.Release.TrySetResult();
            await WaitForCompletionAsync(worker);

            var status = worker.GetStatus();
            status.State.Should().Be(NotionSyncJobStates.Completed);
            status.Source.Should().Be("manual");
            status.Result.Should().BeEquivalentTo(new NotionSyncResult(true, "complete", 2, 3, 0));
            notionSync.SettingsReads.Should().Be(1);
            notionSync.Syncs.Should().Be(1);
        }
        finally
        {
            notionSync.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private static async Task WaitForCompletionAsync(INotionSyncCoordinator coordinator)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (coordinator.GetStatus().IsActive && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        coordinator.GetStatus().IsActive.Should().BeFalse("the queued sync should complete promptly");
    }

    private sealed class FakeNotionSyncService : INotionSyncService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SettingsReads { get; private set; }
        public int Syncs { get; private set; }

        public Task<NotionConnectorSettingsView?> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            SettingsReads++;
            return Task.FromResult<NotionConnectorSettingsView?>(new NotionConnectorSettingsView
            {
                AutoSyncEnabled = true,
                HasStoredIntegrationToken = true,
                IntegrationToken = string.Empty
            });
        }

        public Task<NotionValidationResult> SaveSettingsAsync(
            NotionConnectorSettingsView settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<NotionSyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            Syncs++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new NotionSyncResult(true, "complete", 2, 3, 0);
        }

        public Task<NotionSyncResult> PushPageAsync(
            Guid wikiPageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
