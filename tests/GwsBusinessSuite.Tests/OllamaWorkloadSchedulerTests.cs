using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class OllamaWorkloadSchedulerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task InteractiveWaiter_ShouldRunBeforeQueuedBackgroundWaiter()
    {
        var scheduler = new OllamaWorkloadScheduler();
        var active = await scheduler.AcquireAsync(OllamaWorkloadPriority.Background);
        var background = scheduler.AcquireAsync(OllamaWorkloadPriority.Background).AsTask();
        var interactive = scheduler.AcquireAsync(OllamaWorkloadPriority.Interactive).AsTask();

        background.IsCompleted.Should().BeFalse();
        interactive.IsCompleted.Should().BeFalse();

        await active.DisposeAsync();
        var interactiveLease = await interactive.WaitAsync(TestTimeout);
        background.IsCompleted.Should().BeFalse();

        await interactiveLease.DisposeAsync();
        var backgroundLease = await background.WaitAsync(TestTimeout);
        await backgroundLease.DisposeAsync();
    }

    [Fact]
    public async Task InteractiveArrival_ShouldSignalBackgroundWorkToStandAside_AndHeldSinceShouldTrackTheHolder()
    {
        var scheduler = new OllamaWorkloadScheduler();
        scheduler.HeldSince.Should().BeNull();
        var running = await scheduler.AcquireAsync(OllamaWorkloadPriority.Background);
        scheduler.HeldSince.Should().NotBeNull();
        var standAside = scheduler.InteractiveArrivedToken;

        var queuedBackground = scheduler.AcquireAsync(OllamaWorkloadPriority.Background).AsTask();
        standAside.IsCancellationRequested.Should().BeFalse("more background work doesn't hurry anyone");

        var interactive = scheduler.AcquireAsync(OllamaWorkloadPriority.Interactive).AsTask();
        standAside.IsCancellationRequested.Should().BeTrue();
        scheduler.InteractiveArrivedToken.IsCancellationRequested.Should().BeFalse("a fresh signal for the next arrival");

        await running.DisposeAsync();
        await (await interactive.WaitAsync(TestTimeout)).DisposeAsync();
        await (await queuedBackground.WaitAsync(TestTimeout)).DisposeAsync();
        scheduler.HeldSince.Should().BeNull("nobody holds the model once the queue is empty");
    }

    [Fact]
    public async Task CancelledWaiter_ShouldBeRemovedWithoutBlockingNextWork()
    {
        var scheduler = new OllamaWorkloadScheduler();
        var active = await scheduler.AcquireAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelled = scheduler.AcquireAsync(
            OllamaWorkloadPriority.Interactive,
            cancellation.Token).AsTask();
        cancellation.Cancel();

        await cancelled.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        await active.DisposeAsync();

        var next = await scheduler.AcquireAsync().AsTask().WaitAsync(TestTimeout);
        await next.DisposeAsync();
    }

    [Fact]
    public async Task BackgroundScope_ShouldFlowAcrossAsyncCallsAndRestoreInteractiveDefault()
    {
        var scheduler = new OllamaWorkloadScheduler();
        var active = await scheduler.AcquireAsync(OllamaWorkloadPriority.Interactive);
        Task<IAsyncDisposable> background;
        using (scheduler.UseBackgroundPriority())
        {
            await Task.Yield();
            background = scheduler.AcquireAsync().AsTask();
        }

        var interactive = scheduler.AcquireAsync().AsTask();
        await active.DisposeAsync();

        var interactiveLease = await interactive.WaitAsync(TestTimeout);
        background.IsCompleted.Should().BeFalse();
        await interactiveLease.DisposeAsync();
        var backgroundLease = await background.WaitAsync(TestTimeout);
        await backgroundLease.DisposeAsync();
    }
}
