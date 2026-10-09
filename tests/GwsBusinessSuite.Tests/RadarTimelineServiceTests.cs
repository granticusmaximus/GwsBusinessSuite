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
}
