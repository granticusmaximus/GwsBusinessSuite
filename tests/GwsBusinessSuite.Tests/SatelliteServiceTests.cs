using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// Unit 5 (06-11-26): satellites from CelesTrak's elements, propagated with SGP4. The elements are
// CelesTrak's stations group of 05-11-26 (epoch 26309.85 = 05-11-26 20:28 UTC).
public sealed class SatelliteServiceTests
{
    private const string Stations = """
        ISS (ZARYA)
        1 25544U 98067A   26309.85320381  .00006629  00000+0  12749-3 0  9998
        2 25544  51.6327 319.0560 0007195 352.5747   7.5134 15.49194530593711
        POISK
        1 36086U 09060A   26309.85320381  .00006629  00000+0  12749-3 0  9996
        2 36086  51.6327 319.0560 0007195 352.5747   7.5134 15.49194530586693
        CSS (TIANHE)
        1 48274U 21035A   26309.93077514  .00018706  00000+0  21610-3 0  9996
        2 48274  41.4683 180.7545 0004534 265.1608  94.8714 15.61437978315303
        A STRAY LINE
        """;

    private static readonly DateTime AtEpoch = new(2026, 11, 5, 20, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Elements_ShouldParseThreeLineSets_AndSkipStrayLines() =>
        SatelliteService.ParseTle(Stations).Select(e => e.Name).Should().Equal("ISS (ZARYA)", "POISK", "CSS (TIANHE)");

    [Fact]
    public void Positions_ShouldMergeDockedModules_AndPlaceStationsWhereTheyFly()
    {
        var positions = SatelliteService.Positions(SatelliteService.ParseTle(Stations), "space station", AtEpoch, withPaths: true);

        positions.Select(p => p.Name).Should().Equal("ISS (ZARYA)", "CSS (TIANHE)");
        var iss = positions[0];
        iss.Id.Should().Be("25544");
        iss.With.Should().Equal("POISK");
        iss.AltitudeKm.Should().BeInRange(380, 440, "the ISS flies at about 400 km");
        Math.Abs(iss.Latitude).Should().BeLessThanOrEqualTo(51.7, "it never goes beyond its inclination");
        iss.PeriodMinutes.Should().BeApproximately(92.95, 0.1);
        iss.Path.Should().HaveCount(SatelliteService.PathMinutes / SatelliteService.PathStepMinutes + 1);
        iss.Path[0].Latitude.Should().BeApproximately(iss.Latitude, 0.001);
        iss.Path.Max(p => p.Latitude).Should().BeGreaterThan(45, "an orbit reaches near its inclination");
        positions[1].Latitude.Should().BeInRange(-41.5, 41.5);

        SatelliteService.IsStation("ISS (ZARYA)").Should().BeTrue();
        SatelliteService.IsStation("CSS (TIANHE)").Should().BeTrue();
        SatelliteService.IsStation("FREGAT DEB").Should().BeFalse("the stations group also holds debris near them");
        SatelliteService.Positions(SatelliteService.ParseTle(Stations), "visible", AtEpoch, withPaths: false)
            .Should().OnlyContain(p => p.Path.Count == 0);
    }
}
