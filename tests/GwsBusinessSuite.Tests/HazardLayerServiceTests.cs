using FluentAssertions;
using GwsBusinessSuite.Application.Hazards;

namespace GwsBusinessSuite.Tests;

// Fixtures are trimmed copies of the live feeds captured 2026-10-08.
public sealed class HazardLayerServiceTests
{
    [Fact]
    public void ParseQuakes_ShouldScaleSeverityByMagnitude()
    {
        const string json = """
            {"features":[
              {"id":"us6000u0xn","properties":{"mag":5.1,"place":"236 km E of Levuka, Fiji","time":1791454048642,"url":"https://earthquake.usgs.gov/earthquakes/eventpage/us6000u0xn","alert":null,"tsunami":0},"geometry":{"coordinates":[-178.4545,-18.0147,602.272]}},
              {"id":"ak1","properties":{"mag":2.6,"place":"Alaska","time":1791454048642,"url":"u","tsunami":0},"geometry":{"coordinates":[-150,61,10]}},
              {"id":"big","properties":{"mag":6.4,"place":"Offshore","time":1791454048642,"url":"u","alert":"yellow","tsunami":1},"geometry":{"coordinates":[140,35,20]}},
              {"id":"nomag","properties":{"mag":null,"place":"x"},"geometry":{"coordinates":[1,1,1]}}]}
            """;

        var quakes = HazardLayerService.ParseQuakes(json);

        quakes.Should().HaveCount(3);
        quakes[0].Should().Match<HazardFeature>(q => q.Title == "M5.1 - 236 km E of Levuka, Fiji" && q.Severity == "moderate"
            && q.Latitude == -18.0147 && q.Detail.Contains("depth 602 km"));
        quakes[1].Severity.Should().Be("minor");
        quakes[2].Severity.Should().Be("major");
        quakes[2].Detail.Should().Contain("PAGER alert: yellow").And.Contain("tsunami");
    }

    [Fact]
    public void ParseFires_ShouldReadSizeAndContainment()
    {
        const string json = """
            {"features":[
              {"attributes":{"UniqueFireIdentifier":"2026-CASHU-001","IncidentName":"Ridge","IncidentSize":12500,"PercentContained":40,"FireDiscoveryDateTime":1791000000000,"POOState":"US-CA"},"geometry":{"x":-121.5,"y":40.1}},
              {"attributes":{"UniqueFireIdentifier":"2026-IDBOF-002","IncidentName":"Small","IncidentSize":null,"PercentContained":null},"geometry":{"x":-116,"y":44}},
              {"attributes":{"IncidentName":"No geometry"}}]}
            """;

        var fires = HazardLayerService.ParseFires(json);

        fires.Should().HaveCount(2);
        fires[0].Should().Match<HazardFeature>(f => f.Title == "Ridge Fire" && f.Severity == "major"
            && f.Detail == "12,500 acres · 40% contained · CA" && f.Latitude == 40.1);
        fires[1].Detail.Should().Be("size not reported");
        HazardLayerService.ParseFires("""{"features":[{"attributes":{"IncidentName":"Brewer","IncidentSize":70821,"PercentContained":100},"geometry":{"x":-120,"y":44}}]}""")
            .Single().Severity.Should().Be("minor", "a fully contained fire is no longer spreading");
        fires[1].Severity.Should().Be("minor");
    }

    [Fact]
    public void ParseFloodingGauges_ShouldKeepOnlyGaugesAtOrAboveActionStage()
    {
        const string json = """
            {"gauges":[
              {"lid":"AANG1","name":"Peachtree Creek at Atlanta","latitude":33.82,"longitude":-84.40,"status":{"observed":{"primary":2.16,"primaryUnit":"ft","floodCategory":"no_flooding","validTime":"2026-10-08T12:00:00Z"}}},
              {"lid":"FLDG1","name":"Flint River at Albany","latitude":31.58,"longitude":-84.15,"status":{"observed":{"primary":24.3,"primaryUnit":"ft","floodCategory":"moderate","validTime":"2026-10-08T12:00:00Z"}}},
              {"lid":"ACTG1","name":"Ocmulgee at Macon","latitude":32.84,"longitude":-83.62,"status":{"observed":{"primary":18,"primaryUnit":"ft","floodCategory":"action","validTime":"2026-10-08T12:00:00Z"}}},
              {"lid":"OOSG1","name":"Out","latitude":32,"longitude":-83,"status":{"observed":{"floodCategory":"out_of_service"}}}]}
            """;

        var gauges = HazardLayerService.ParseFloodingGauges(json);

        gauges.Select(g => (g.Id, g.Severity)).Should().Equal(("gauge-FLDG1", "moderate"), ("gauge-ACTG1", "minor"));
        gauges[0].Detail.Should().Be("moderate flooding · stage 24.3 ft");
        gauges[0].Url.Should().Be("https://water.noaa.gov/gauges/FLDG1");
        gauges[1].Detail.Should().StartWith("at action stage");
    }
}
