using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class OllamaWorkloadSchedulerTests
{
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
        var interactiveLease = await interactive.WaitAsync(TimeSpan.FromSeconds(1));
        background.IsCompleted.Should().BeFalse();

        await interactiveLease.DisposeAsync();
        var backgroundLease = await background.WaitAsync(TimeSpan.FromSeconds(1));
        await backgroundLease.DisposeAsync();
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

        var next = await scheduler.AcquireAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
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

        var interactiveLease = await interactive.WaitAsync(TimeSpan.FromSeconds(1));
        background.IsCompleted.Should().BeFalse();
        await interactiveLease.DisposeAsync();
        var backgroundLease = await background.WaitAsync(TimeSpan.FromSeconds(1));
        await backgroundLease.DisposeAsync();
    }
}
