using FluentAssertions;
using GwsBusinessSuite.Application.RouteWatch;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Application.Weather;

namespace GwsBusinessSuite.Tests;

public sealed class DeparturePlannerTests
{
    // 00:00 UTC = 20:00 New York the evening before; the drive is ~100 mi due north (no sun at night).
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 10, 0, TimeSpan.Zero);

    private static RouteWatchResult Route(params RouteIncident[] incidents)
    {
        var path = Enumerable.Range(0, 101).Select(i => new RoutePoint(33.0 + i * (1.45 / 100), -84.0)).ToList();
        return new RouteWatchResult("A", "B", 100, 100, path, [], incidents, []);
    }

    private static HourlyForecastPeriod Hour(DateTimeOffset start, int chance, string text, double temperature = 60) =>
        new(start, start.AddHours(1), temperature, chance, text, "5 mph", "N");

    // A forecast for every sample point: dry, except rain at the middle of the route from 03:00 to 06:00 UTC.
    private static IReadOnlyList<RouteForecastPoint> Forecast(RouteWatchResult route, Func<double, DateTimeOffset, HourlyForecastPeriod> hourAt) =>
        DeparturePlanner.SamplePoints(route.Path).Select(sample => new RouteForecastPoint(sample.Mile, sample.Point.Latitude, sample.Point.Longitude,
            Enumerable.Range(0, 30).Select(h => hourAt(sample.Mile, new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero).AddHours(h))).ToList())).ToList();

    [Fact]
    public void Plan_ShouldTryEveryHalfHourForTwelveHours_AndPreferTheDryTime()
    {
        var route = Route();
        var forecast = Forecast(route, (mile, start) =>
            mile is > 30 and < 70 && start.Hour is >= 3 and < 6 ? Hour(start, 90, "Rain Showers") : Hour(start, 0, "Clear"));

        var plan = DeparturePlanner.Plan(route, forecast, Now);

        plan.Options.Should().HaveCount(25);
        plan.Options[0].DepartUtc.Should().Be(new DateTimeOffset(2026, 10, 12, 0, 30, 0, TimeSpan.Zero));
        var wet = plan.Options.Single(o => o.DepartUtc == new DateTimeOffset(2026, 10, 12, 3, 30, 0, TimeSpan.Zero));
        wet.Rating.Should().NotBe(DepartureRatings.Good);
        wet.Reasons[0].Should().Contain("90% chance of rain").And.Contain("Rain Showers");
        plan.Best!.Score.Should().BeLessThan(wet.Score);
        plan.Best.Rating.Should().Be(DepartureRatings.Good);
        plan.Note.Should().Contain("rush-hour traffic isn't included");
    }

    [Fact]
    public void Score_ShouldWeighSnowAndNearFreezingRainAboveRain()
    {
        var route = Route();
        var depart = new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);
        var rain = DeparturePlanner.Score(route, Forecast(route, (_, s) => Hour(s, 60, "Rain")), depart, Now);
        var snow = DeparturePlanner.Score(route, Forecast(route, (_, s) => Hour(s, 60, "Snow Showers", 28)), depart, Now);
        var coldRain = DeparturePlanner.Score(route, Forecast(route, (_, s) => Hour(s, 60, "Rain", 33)), depart, Now);

        snow.Score.Should().BeGreaterThan(rain.Score * 2);
        coldRain.Score.Should().Be(snow.Score);
        snow.Reasons[0].Should().StartWith("Snow/ice risk");
        snow.Rating.Should().Be(DepartureRatings.Poor);
    }

    [Fact]
    public void Score_ShouldCountAClosureOnlyWhileItLasts_AndCrashesOnlySoon()
    {
        var closure = new TrafficIncident("c1", "I 75", "Bridge closed", "closure", "major", 33.7, -84, "GDOT", "https://511ga.org",
            Category: IncidentCategories.Closure, EndsAt: new DateTimeOffset(2026, 10, 12, 4, 0, 0, TimeSpan.Zero));
        var crash = new TrafficIncident("x1", "I 75", "Crash", "crash", "major", 33.5, -84, "GDOT", "https://511ga.org", Category: IncidentCategories.Crash);
        var route = Route(new RouteIncident(closure, 50, 0.1), new RouteIncident(crash, 20, 0.1));
        var dry = Forecast(route, (_, s) => Hour(s, 0, "Clear"));

        var soon = DeparturePlanner.Score(route, dry, new DateTimeOffset(2026, 10, 12, 0, 30, 0, TimeSpan.Zero), Now);
        var later = DeparturePlanner.Score(route, dry, new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero), Now);

        soon.Reasons.Should().Contain(r => r.StartsWith("Closure at mile 50")).And.Contain(r => r.StartsWith("Crash at mile 20"));
        later.Reasons.Should().NotContain(r => r.StartsWith("Closure") || r.StartsWith("Crash"));
        later.Score.Should().BeLessThan(soon.Score - 2);
    }

    [Fact]
    public void Plan_WithoutAForecast_ShouldSayWhatItCouldNotCheck()
    {
        var route = Route();
        var plan = DeparturePlanner.Plan(route, DeparturePlanner.SamplePoints(route.Path)
            .Select(p => new RouteForecastPoint(p.Mile, p.Point.Latitude, p.Point.Longitude, [])).ToList(), Now);

        plan.Note.Should().StartWith("No NWS forecast");
        plan.Options.Should().OnlyContain(o => o.Rating == DepartureRatings.Good);
    }

    [Fact]
    public void SamplePoints_ShouldCoverBothEnds_EveryTwentyFiveMiles()
    {
        var samples = DeparturePlanner.SamplePoints(Route().Path);

        samples[0].Mile.Should().Be(0);
        samples[^1].Mile.Should().BeApproximately(100, 1);
        samples.Should().HaveCountGreaterThanOrEqualTo(5);
    }
}
