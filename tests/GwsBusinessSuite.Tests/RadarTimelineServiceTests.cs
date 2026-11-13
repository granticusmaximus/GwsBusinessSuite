using FluentAssertions;
using GwsBusinessSuite.Application.Weather;

namespace GwsBusinessSuite.Tests;

public sealed class RadarTimelineServiceTests
{
    private static readonly DateTimeOffset LatestScan = new(2026, 10, 9, 13, 40, 0, TimeSpan.Zero);
    // A 2-hour-40-minute-old HRRR run, as the real feed usually is when it appears.
    private static readonly DateTimeOffset ModelRun = new(2026, 10, 9, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_ShouldPutTheLastHourOfScansBeforeNow_OldestFirst()
    {
        var timeline = RadarTimelineService.Build(LatestScan, ModelRun, LatestScan.AddMinutes(2));

        var observed = timeline.Frames.Where(f => f.Kind == RadarFrameKind.Observed).ToList();
        observed.Should().HaveCount(12);
        observed[0].ValidUtc.Should().Be(LatestScan.AddMinutes(-55));
        observed[0].TileUrlTemplate.Should().EndWith("nexrad-n0q-900913-m55m/{z}/{x}/{y}.png");
        observed[^1].ValidUtc.Should().Be(LatestScan);
        observed[^1].TileUrlTemplate.Should().EndWith("nexrad-n0q-900913/{z}/{x}/{y}.png");
        timeline.NowIndex.Should().Be(11);
        timeline.Frames[timeline.NowIndex].ValidUtc.Should().Be(LatestScan);
    }

    [Fact]
    public void Build_ShouldDropForecastFramesAlreadyInThePast_AndLabelTheRestByValidTime()
    {
        var timeline = RadarTimelineService.Build(LatestScan, ModelRun, LatestScan.AddMinutes(2));

        var forecast = timeline.Frames.Where(f => f.Kind == RadarFrameKind.Forecast).ToList();
        forecast.Should().OnlyContain(f => f.ValidUtc > LatestScan);
        forecast[0].ValidUtc.Should().Be(new DateTimeOffset(2026, 10, 9, 13, 45, 0, TimeSpan.Zero));
        forecast[0].TileUrlTemplate.Should().EndWith("hrrr::REFD-F0165-0/{z}/{x}/{y}.png", "13:45 is 165 minutes into the 11Z run");
        forecast[^1].ValidUtc.Should().Be(ModelRun.AddHours(18));
        timeline.ModelRunUtc.Should().Be(ModelRun);
    }

    [Fact]
    public void Build_ShouldStep15MinutesForThreeHours_ThenHourly()
    {
        var timeline = RadarTimelineService.Build(LatestScan, ModelRun, LatestScan);

        var forecastTimes = timeline.Frames.Where(f => f.Kind == RadarFrameKind.Forecast).Select(f => f.ValidUtc).ToList();
        var steps = forecastTimes.Zip(forecastTimes.Skip(1), (a, b) => (At: a, Step: b - a)).ToList();
        steps.Where(s => s.At < LatestScan.AddHours(2.5)).Should().OnlyContain(s => s.Step == TimeSpan.FromMinutes(15));
        steps.Where(s => s.At >= LatestScan.AddHours(3.5)).Should().OnlyContain(s => s.Step == TimeSpan.FromHours(1));
        forecastTimes.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Build_WithoutAModelRun_ShouldStillOfferTheObservedLoop()
    {
        var timeline = RadarTimelineService.Build(LatestScan, modelRunUtc: null, LatestScan);

        timeline.Frames.Should().HaveCount(12).And.OnlyContain(f => f.Kind == RadarFrameKind.Observed);
        timeline.ModelRunUtc.Should().BeNull();
    }

    [Fact]
    public void Build_WithoutTheScanTime_ShouldEstimateItFromTheClock()
    {
        var now = new DateTimeOffset(2026, 10, 9, 13, 43, 20, TimeSpan.Zero);

        var timeline = RadarTimelineService.Build(latestScanUtc: null, ModelRun, now);

        timeline.Frames[timeline.NowIndex].ValidUtc.Should().Be(new DateTimeOffset(2026, 10, 9, 13, 35, 0, TimeSpan.Zero));
    }

    // Worldwide radar (12-11-26): each observed frame carries the RainViewer frame nearest its time,
    // within 10 minutes, to draw under the US layer. Forecast frames stay US-only.
    [Fact]
    public void Build_ShouldPairEachScanWithTheNearestWorldFrame_AndLeaveForecastsUsOnly()
    {
        var world = RadarTimelineService.ParseGlobalFrames("https://tilecache.rainviewer.com",
            Enumerable.Range(0, 13).Select(i => (LatestScan.AddMinutes(-120 + i * 10).ToUnixTimeSeconds(), (string?)$"/v2/radar/f{i}")));

        var timeline = RadarTimelineService.Build(LatestScan, ModelRun, LatestScan.AddMinutes(2), world);

        var now = timeline.Frames[timeline.NowIndex];
        now.GlobalTileUrlTemplate.Should().Be("https://tilecache.rainviewer.com/v2/radar/f12/256/{z}/{x}/{y}/2/1_1.png");
        timeline.Frames[timeline.NowIndex - 1].GlobalTileUrlTemplate.Should().NotBeNull("5 minutes off is still the nearest world frame");
        timeline.Frames.Where(frame => frame.Kind == RadarFrameKind.Observed).Should().OnlyContain(frame => frame.GlobalTileUrlTemplate != null);
        timeline.Frames.Where(frame => frame.Kind == RadarFrameKind.Forecast).Should().OnlyContain(frame => frame.GlobalTileUrlTemplate == null);

        RadarTimelineService.Build(LatestScan, ModelRun, LatestScan, world.Where(frame => frame.ValidUtc < LatestScan.AddMinutes(-30)).ToList())
            .Frames[timeline.NowIndex].GlobalTileUrlTemplate.Should().BeNull("no world frame within 10 minutes");
    }

    [Fact]
    public void ParseGlobalFrames_ShouldOnlyTrustHttpsHostsAndPlainPaths()
    {
        RadarTimelineService.ParseGlobalFrames("http://tilecache.rainviewer.com", [(1, "/v2/radar/a")]).Should().BeEmpty();
        RadarTimelineService.ParseGlobalFrames(null, [(1, "/v2/radar/a")]).Should().BeEmpty();
        RadarTimelineService.ParseGlobalFrames("https://tilecache.rainviewer.com", [(1, "/../x"), (2, "relative"), (0, "/v2/radar/a")]).Should().BeEmpty();
        RadarTimelineService.ParseGlobalFrames("https://tilecache.rainviewer.com/", [(20, "/v2/radar/b"), (10, "/v2/radar/a")])
            .Select(frame => frame.TileUrlTemplate).Should().Equal(
                "https://tilecache.rainviewer.com/v2/radar/a/256/{z}/{x}/{y}/2/1_1.png",
                "https://tilecache.rainviewer.com/v2/radar/b/256/{z}/{x}/{y}/2/1_1.png");
    }
}
