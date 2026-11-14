using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// #4: the worldwide convergence view is shared and each viewer filters it to their view.
[Collection("ConvergenceWorld")]
public sealed class ConvergenceWorldTests
{
    // Long ago, so a stored view is never "fresh" for another test running on the real clock.
    private static readonly DateTimeOffset Now = new(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ConvergenceCell Cell(double south, double west) =>
        new(south, west, [], 3, "moderate", []);

    [Fact]
    public void Within_ShouldKeepOnlyWhatTouchesTheView()
    {
        var world = new ConvergenceView([Cell(33, -85), Cell(51, 0)], [new ReportingBlackout(33.5, -84.5, "internet", "x", "y", null), new ReportingBlackout(-10, 20, "internet", "x", "y", null)]);

        var view = ConvergenceWorld.Within(world, new BoundingBox(40, 30, -80, -90));

        Assert.Single(view.Cells);
        Assert.Single(view.Blackouts);
    }

    [Fact]
    public void Refresh_ShouldRunOnlyWhileWanted_AndTheViewGoesStale()
    {
        ConvergenceWorld.Reset();
        Assert.False(ConvergenceWorld.IsWanted(Now));
        ConvergenceWorld.Wanted(Now);
        Assert.True(ConvergenceWorld.IsWanted(Now.AddMinutes(29)));
        Assert.False(ConvergenceWorld.IsWanted(Now.AddMinutes(31)));

        Assert.True(ConvergenceWorld.IsDue(Now));
        ConvergenceWorld.Store(new ConvergenceView([], []), Now);
        Assert.False(ConvergenceWorld.IsDue(Now.AddMinutes(4)));
        Assert.True(ConvergenceWorld.IsDue(Now.AddMinutes(5)));
        Assert.NotNull(ConvergenceWorld.Fresh(Now.AddMinutes(11)));
        Assert.Null(ConvergenceWorld.Fresh(Now.AddMinutes(13)));
        ConvergenceWorld.Reset();
    }

    [Fact]
    public void IsWide_ShouldLeaveCloseViewsToTheirOwnSharperCalculation()
    {
        Assert.True(ConvergenceWorld.IsWide(new BoundingBox(60, 20, -60, -130)));
        Assert.False(ConvergenceWorld.IsWide(new BoundingBox(34.5, 33, -83.5, -85)));
    }
}
