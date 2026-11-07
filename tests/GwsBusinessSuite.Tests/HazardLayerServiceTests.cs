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

    // Trimmed from the live GDACS SEARCH feed, 05-11-26.
    [Fact]
    public void ParseGdacs_ShouldKeepCurrentEvents_MapTheAlertLevel_AndLeaveUsWildfiresToNifc()
    {
        const string json = """
            {"type":"FeatureCollection","features":[
              {"geometry":{"type":"Point","coordinates":[80.2,15.8]},"properties":{"eventtype":"TC","eventid":1001346,"name":"Tropical Cyclone MURASU-26","alertlevel":"Red","iscurrent":"true","iso3":"IND","country":"India","datemodified":"2026-11-04T06:01:20","severitydata":{"severitytext":"Hurricane/Typhoon > 74 mph (maximum wind speed of 157 km/h)"},"url":{"report":"https://www.gdacs.org/report.aspx?eventid=1001346&episodeid=10&eventtype=TC"}}},
              {"geometry":{"type":"Point","coordinates":[121.1,14.0]},"properties":{"eventtype":"VO","eventid":1000151,"name":"Eruption  Taal","alertlevel":"Orange","iscurrent":"true","iso3":"PHL","country":"Philippines","datemodified":"2026-11-05T10:29:00","severitydata":{"severitytext":""},"url":{"report":"https://www.gdacs.org/report.aspx?eventid=1000151"}}},
              {"geometry":{"type":"Point","coordinates":[23.4,-21.2]},"properties":{"eventtype":"WF","eventid":1027700,"name":"Forest fires in Botswana","alertlevel":"Green","iscurrent":"true","iso3":"BWA","country":"Botswana","datemodified":"2026-11-05T00:00:00","severitydata":{"severitytext":"Green impact for forestfire in 105482 ha"},"url":{"report":"https://www.gdacs.org/report.aspx?eventid=1027700"}}},
              {"geometry":{"type":"Point","coordinates":[-120.5,38.9]},"properties":{"eventtype":"WF","eventid":1027701,"name":"Forest fires in United States","alertlevel":"Green","iscurrent":"true","iso3":"USA","country":"United States"}},
              {"geometry":{"type":"Point","coordinates":[15.0,45.0]},"properties":{"eventtype":"DR","eventid":1018332,"name":"Drought in Andorra, Albania, Austria","alertlevel":"Orange","iscurrent":"true","iso3":"AND","country":"Andorra, Albania, Austria"}},
              {"geometry":{"type":"Point","coordinates":[100.0,30.0]},"properties":{"eventtype":"EQ","eventid":1573141,"name":"Earthquake in China","alertlevel":"Orange","iscurrent":"true"}},
              {"geometry":{"type":"Point","coordinates":[150.0,-30.0]},"properties":{"eventtype":"FL","eventid":1103001,"name":"Flood in Australia","alertlevel":"Orange","iscurrent":"false"}},
              {"geometry":{"type":"Point","coordinates":[25.0,42.7]},"properties":{"eventtype":"FL","eventid":1103002,"name":"Flood in Bulgaria","alertlevel":"Green","iscurrent":"true","country":"Bulgaria","severitydata":{"severitytext":"Magnitude 0 "}}}]}
            """;

        var events = HazardLayerService.ParseGdacs(json);

        events.Select(e => e.Kind).Should().Equal(HazardKinds.Cyclone, HazardKinds.Volcano, HazardKinds.Wildfire, HazardKinds.Drought, HazardKinds.Flood);
        var cyclone = events[0];
        cyclone.Should().Match<HazardFeature>(e => e.Id == "gdacs-TC-1001346" && e.Severity == "major" && e.Latitude == 15.8 && e.Longitude == 80.2
            && e.Url.Contains("eventid=1001346"));
        cyclone.Detail.Should().Be("Hurricane/Typhoon > 74 mph (maximum wind speed of 157 km/h) · India · GDACS red alert");
        events[1].Title.Should().Be("Eruption Taal", "GDACS's doubled spaces are tidied");
        events[1].Severity.Should().Be("moderate");
        events[2].Detail.Should().NotContain("Botswana ·", "the country is already in the name");
        events[2].Severity.Should().Be("minor");
        events[3].Detail.Should().EndWith("pin at the centre of the drought area");
        events[3].Url.Should().Be("https://www.gdacs.org");
        events[4].Detail.Should().Be("GDACS green alert", "a flood's \"Magnitude 0\" says nothing");
    }

    // 07-11-26: NOAA's National Hurricane Center (live: Tropical Storm Pena).
    [Fact]
    public void ParseNhc_ShouldDescribeEachStorm_AndRateHurricanesByWind()
    {
        const string json = """
            {"activeStorms":[
              {"id":"cp042026","name":"Pena","classification":"TS","intensity":"45","pressure":"998","latitudeNumeric":11.5,"longitudeNumeric":-153.9,
               "movementDir":285,"movementSpeed":9,"lastUpdate":"2026-11-07T21:00:00.000Z","publicAdvisory":{"url":"https://www.nhc.noaa.gov/text/HFOTCPCP4.shtml"}},
              {"id":"al142026","name":"Tony","classification":"HU","intensity":"105","pressure":"960","latitudeNumeric":25.4,"longitudeNumeric":-71.2},
              {"id":"bad","name":"NoPlace","classification":"TS"}
            ]}
            """;

        var storms = HazardLayerService.ParseNhc(json);

        storms.Should().HaveCount(2, "a storm without a position is skipped");
        storms[0].Should().Match<HazardFeature>(s => s.Kind == HazardKinds.Cyclone && s.Title == "Tropical Storm Pena" && s.Severity == "minor"
            && s.Detail == "45 kt winds · 998 mb · moving W at 9 mph · NOAA National Hurricane Center"
            && s.Url == "https://www.nhc.noaa.gov/text/HFOTCPCP4.shtml");
        storms[1].Should().Match<HazardFeature>(s => s.Title == "Hurricane Tony" && s.Severity == "major" && s.Url == "https://www.nhc.noaa.gov/",
            "105 kt is category 3");
    }

    // 07-11-26: NASA EONET, for the natural events nothing else here covers (live: 35 icebergs).
    [Fact]
    public void ParseEonet_ShouldKeepOnlyWhatNothingElseCovers_AtTheLatestPoint()
    {
        const string json = """
            {"events":[
              {"id":"EONET_1","title":"Iceberg B22K","categories":[{"id":"seaLakeIce"}],"sources":[{"url":"https://usicecenter.gov/pub/Iceberg_Tabular.csv"}],
               "geometry":[{"date":"2026-10-01T00:00:00Z","type":"Point","coordinates":[-100,-70]},{"date":"2026-10-30T00:00:00Z","type":"Point","coordinates":[-101.5,-71.2]}]},
              {"id":"EONET_2","title":"Landslide in Nepal","categories":[{"id":"landslides"}],"link":"https://eonet.gsfc.nasa.gov/api/v3/events/EONET_2",
               "geometry":[{"date":"2026-11-01T00:00:00Z","type":"Point","coordinates":[85.3,27.7]}]},
              {"id":"EONET_3","title":"Wildfire","categories":[{"id":"wildfires"}],"geometry":[{"date":"2026-11-01T00:00:00Z","type":"Point","coordinates":[1,1]}]},
              {"id":"EONET_4","title":"Dust area","categories":[{"id":"dustHaze"}],
               "geometry":[{"date":"2026-11-01T00:00:00Z","type":"Polygon","coordinates":[[[1,1],[2,2],[3,1],[1,1]]]}]}
            ]}
            """;

        var events = HazardLayerService.ParseEonet(json);

        events.Should().HaveCount(2, "wildfires are covered by NIFC and GDACS, and an event given only as an area is left out");
        events[0].Should().Match<HazardFeature>(e => e.Kind == HazardKinds.Ice && e.Latitude == -71.2 && e.Longitude == -101.5 && e.Severity == "minor"
            && e.Url == "https://usicecenter.gov/pub/Iceberg_Tabular.csv");
        events[1].Should().Match<HazardFeature>(e => e.Kind == HazardKinds.Landslide && e.Severity == "moderate"
            && e.Url == "https://eonet.gsfc.nasa.gov/api/v3/events/EONET_2");
    }
}
