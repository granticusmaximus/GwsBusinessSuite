using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.RoadTraffic;

namespace GwsBusinessSuite.Tests;

// Finland's traffic stations on Overwatch's city traffic (Digitraffic TMS, 31-10-26). Shapes from
// the live feeds (tie.digitraffic.fi/api/tms/v1/stations and .../stations/data, 31-10-26).
public sealed class DigitrafficTmsSpeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 13, 12, 0, TimeSpan.Zero);

    private const string StationsJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","id":20002,"geometry":{"type":"Point","coordinates":[24.73,60.2,0.0]},
           "properties":{"id":20002,"name":"vt1_Espoo_Hirvisuo","bearing":298,"collectionStatus":"GATHERING"}},
          {"type":"Feature","id":89,"geometry":{"type":"Point","coordinates":[23.58,61.47,0.0]},
           "properties":{"id":89,"name":"DSL6L","bearing":null,"collectionStatus":"REMOVED_TEMPORARILY"}},
          {"type":"Feature","id":23001,"geometry":{"type":"Point","coordinates":[25.66,60.39,0.0]},
           "properties":{"id":23001,"name":"vt7_Rita","bearing":null,"collectionStatus":"GATHERING"}},
          {"type":"Feature","id":30001,"geometry":{"type":"Point","coordinates":[22.2,60.45,0.0]},
           "properties":{"id":30001,"name":"kt40_Turku_Kausela","bearing":90,"collectionStatus":"GATHERING"}}
        ]}
        """;

    private const string ReadingsJson = """
        {"dataUpdatedTime":"2026-10-31T13:10:23Z","stations":[
          {"id":20002,"sensorValues":[
            {"name":"KESKINOPEUS_5MIN_LIUKUVA_SUUNTA1","measuredTime":"2026-10-31T13:10:16Z","unit":"km/h","value":97.0},
            {"name":"KESKINOPEUS_5MIN_LIUKUVA_SUUNTA2","measuredTime":"2026-10-31T13:10:16Z","unit":"km/h","value":38.0},
            {"name":"OHITUKSET_5MIN_LIUKUVA_SUUNTA1","measuredTime":"2026-10-31T13:10:16Z","unit":"kpl/h","value":1476.0},
            {"name":"OHITUKSET_5MIN_LIUKUVA_SUUNTA2","measuredTime":"2026-10-31T13:10:16Z","unit":"kpl/h","value":900.0},
            {"name":"KESKINOPEUS_5MIN_LIUKUVA_SUUNTA1_VVAPAAS1","measuredTime":"2026-10-31T13:10:16Z","unit":"***","value":93.0},
            {"name":"KESKINOPEUS_5MIN_LIUKUVA_SUUNTA2_VVAPAAS2","measuredTime":"2026-10-31T13:10:16Z","unit":"***","value":40.0}]},
          {"id":89,"sensorValues":[]},
          {"id":23001,"sensorValues":[
            {"name":"KESKINOPEUS_5MIN_LIUKUVA_SUUNTA1","measuredTime":"2026-10-31T13:10:16Z","unit":"km/h","value":102.0}]},
          {"id":30001,"sensorValues":[
            {"name":"KESKINOPEUS_5MIN_LIUKUVA_SUUNTA1","measuredTime":"2026-10-31T11:00:00Z","unit":"km/h","value":80.0}]}
        ]}
        """;

    [Fact]
    public void Stations_ShouldKeepOnlyCollectingOnes_WithReadableNames()
    {
        var stations = DigitrafficTmsSpeedProvider.ParseStations(StationsJson);

        stations.Keys.Should().BeEquivalentTo([20002, 23001, 30001], "a station removed for now isn't shown");
        stations[20002].Should().BeEquivalentTo(new { Name = "Road 1, Espoo Hirvisuo", Latitude = 60.2, Longitude = 24.73, Bearing = (double?)298 });
        stations[30001].Name.Should().Be("Road 40, Turku Kausela");
    }

    [Fact]
    public void Readings_ShouldShowEachStationsSlowerDirection_WhileItsRecent()
    {
        var links = DigitrafficTmsSpeedProvider.ParseReadings(ReadingsJson, DigitrafficTmsSpeedProvider.ParseStations(StationsJson), Now);

        links.Select(l => l.Id).Should().Equal(["fi-20002", "fi-23001"], "Turku's last reading is two hours old");
        var espoo = links[0];
        espoo.SpeedMph.Should().BeApproximately(38 * 0.621371, 0.1, "the slower of 97 and 38 km/h - the congested side");
        espoo.VehiclesPerHour.Should().Be(900);
        espoo.PercentOfFreeFlow.Should().Be(40);
        espoo.Area.Should().Be("Finland");
        espoo.AsOf.Should().Be(new DateTimeOffset(2026, 10, 31, 13, 10, 16, TimeSpan.Zero));

        // Drawn as a ~700 m stretch through the station, along its bearing (298°: west-north-west).
        espoo.Path.Should().HaveCount(2);
        var (start, end) = (espoo.Path[0], espoo.Path[1]);
        end.Longitude.Should().BeLessThan(start.Longitude);
        end.Latitude.Should().BeGreaterThan(start.Latitude);
        ((start.Longitude + end.Longitude) / 2).Should().BeApproximately(24.73, 1e-9);

        links[1].Should().BeEquivalentTo(new { VehiclesPerHour = (int?)null, PercentOfFreeFlow = (int?)null }, "Rita reports only a speed");
    }

    [Theory]
    [InlineData("vt1_Espoo_Hirvisuo", "Road 1, Espoo Hirvisuo")]
    [InlineData("mt1050_Rajakylä", "Road 1050, Rajakylä")]
    [InlineData("vt4", "Road 4")]
    [InlineData("DSL6L", "DSL6L")]
    [InlineData("", "Traffic station")]
    public void StationNames_ShouldReadAsRoads(string raw, string expected) =>
        DigitrafficTmsSpeedProvider.ReadableName(raw).Should().Be(expected);

    [Fact]
    public void OnlyAViewOverFinland_ShouldAskDigitraffic()
    {
        var provider = new DigitrafficTmsSpeedProvider(new HttpClient(), new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<DigitrafficTmsSpeedProvider>.Instance);

        provider.Coverage.Intersects(new BoundingBox(60.3, 60.1, 25.1, 24.8)).Should().BeTrue("Helsinki");
        provider.Coverage.Intersects(new BoundingBox(59.5, 59.3, 18.2, 17.9)).Should().BeFalse("Stockholm");
    }
}
