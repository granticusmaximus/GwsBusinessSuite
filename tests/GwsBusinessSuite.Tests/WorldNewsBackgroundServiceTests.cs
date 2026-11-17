using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class WorldNewsBackgroundServiceTests
{
    [Fact]
    public async Task WorkerLoop_WhenAnIterationFails_ShouldRetryInsteadOfStopping()
    {
        using var stopped = new CancellationTokenSource();
        var attempts = 0;

        await WorldNewsBackgroundService.RunResilientLoopAsync(
            _ =>
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("transient failure");
                stopped.Cancel();
                return Task.FromResult(TimeSpan.Zero);
            },
            "test",
            TimeSpan.Zero,
            NullLogger.Instance,
            stopped.Token);

        Assert.Equal(2, attempts);
    }
}
