using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// One hung source held Overwatch's incidents and city traffic for 30 s (production). Now what has
// arrived by the deadline is drawn, in the sources' order, and the slow one finishes in the background.
public sealed class SourceDeadlineTests
{
    private static Task<IReadOnlyList<int>> After(int ms, params int[] values) =>
        Task.Delay(ms).ContinueWith(_ => (IReadOnlyList<int>)values);

    [Fact]
    public async Task Gather_ShouldReturnWhatArrivedByTheDeadline_InOrder_WithoutWaitingForTheSlowOne()
    {
        var hung = new TaskCompletionSource<IReadOnlyList<int>>();
        var started = System.Diagnostics.Stopwatch.StartNew();

        var results = await SourceDeadline.GatherAsync(
            [("fast", After(10, 1, 2)), ("hung", hung.Task), ("quick", After(30, 3))],
            TimeSpan.FromMilliseconds(400), NullLogger.Instance, "incidents");

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Equal([1, 2, 3], results.SelectMany(r => r));
        Assert.Empty(results[1]);
        hung.SetResult([9]);
    }

    [Fact]
    public async Task Gather_ShouldReturnAtOnce_WhenEverySourceIsDone()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var results = await SourceDeadline.GatherAsync([("a", After(5, 1)), ("b", After(5, 2))], TimeSpan.FromSeconds(30), NullLogger.Instance, "cameras");

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal([1, 2], results.SelectMany(r => r));
    }
}
