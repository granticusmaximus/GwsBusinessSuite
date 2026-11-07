using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// Overwatch's key-less world layers (07-11-26): travel advice, fire hotspots, conflict and
// humanitarian emergencies, and the satellites' public data. Fixtures are trimmed copies of what each
// source answered that day.
public sealed class WorldLayerServicesTests
{
    private static readonly WorldNewsPlaces Places = WorldNewsPlaces.Instance;
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 21, 30, 0, TimeSpan.Zero);

    [Fact]
    public void TravelAdvice_ShouldPlaceEachCountryByName_AndPinOnlyLevelsTwoToFour()
    {
        // The feed's FIPS codes (YM is Yemen) aren't used; names are, however the State Department writes them.
        const string json = """
            [
              {"Title":"Yemen - Level 4: Do Not Travel","Link":"https://travel.state.gov/yemen","Category":["YM"],"Updated":"2026-10-22T20:00:00-04:00"},
              {"Title":"Mexico Travel Advisory - Level 2: Exercise Increased Caution","Link":"https://travel.state.gov/mexico","Category":["MX"]},
              {"Title":"Burma (Myanmar) - Level 4: Do Not Travel","Link":"https://travel.state.gov/burma","Category":["BM"]},
              {"Title":"The Bahamas - Level 2: Exercise Increased Caution","Link":"https://travel.state.gov/bahamas","Category":["BF"]},
              {"Title":"Jordan - Level 3: Reconsider Travel","Link":"https://travel.state.gov/jordan","Category":["JO"]},
              {"Title":"Gaza - Level 4: Do Not Travel","Link":"https://travel.state.gov/gaza","Category":["GZ"]},
              {"Title":"Canada - Level 1: Exercise Normal Precautions","Link":"https://travel.state.gov/canada","Category":["CA"]},
              {"Title":"Guadeloupe - Level 1: Exercise Normal Precautions","Link":"https://travel.state.gov/guadeloupe","Category":["GP"]},
              {"Title":"Worldwide Caution","Link":"https://travel.state.gov/worldwide"}
            ]
            """;

        var advice = TravelAdvisoryService.Parse(json, Places);

        advice.Select(a => (a.Code, a.Level)).Should().BeEquivalentTo(new[]
        {
            ("YE", 4), ("MX", 2), ("MM", 4), ("BS", 2), ("JO", 3), ("PS", 4), ("CA", 1)
        }, "Guadeloupe isn't a country in the places table, and a title without a level is skipped");
        advice.Single(a => a.Code == "YE").Should().Match<TravelAdvisory>(a => a.Advice == "Do Not Travel" && a.Url == "https://travel.state.gov/yemen"
            && a.Updated == new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.FromHours(-4)));

        var pins = TravelAdvisoryService.Pins(advice, Places);
        pins.Should().HaveCount(6, "level 1 isn't drawn");
        pins.Single(p => p.Id == "travel-YE").Should().Match<HazardFeature>(p => p.Kind == HazardKinds.Travel && p.Severity == "major"
            && p.Title.EndsWith(": Level 4") && p.Detail == "Do Not Travel · US State Department" && p.Latitude == Places.Country("YE")!.Latitude);
        pins.Single(p => p.Id == "travel-JO").Severity.Should().Be("moderate");
        pins.Single(p => p.Id == "travel-MX").Severity.Should().Be("minor");
    }

    [Fact]
    public async Task FireHotspots_ShouldGroupDetectionsIntoCells_DroppingLowConfidence_StrongestFirst()
    {
        const string viirs = """
            latitude,longitude,bright_ti4,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_ti5,frp,daynight
            -6.76,111.96,330.1,0.4,0.4,2026-11-07,1813,N20,VIIRS,n,2.0NRT,290.1,12.5,N
            -6.80,111.90,331.0,0.4,0.4,2026-11-07,1813,N20,VIIRS,h,2.0NRT,290.1,7.5,N
            -6.70,111.99,320.0,0.4,0.4,2026-11-07,0402,N20,VIIRS,l,2.0NRT,290.1,500,D
            40.10,-120.20,340.0,0.4,0.4,2026-11-07,2105,N20,VIIRS,h,2.0NRT,290.1,1250,N
            """;
        const string modis = """
            latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
            40.20,-120.40,330.0,1.0,1.0,2026-11-07,1929,T,MODIS,85,6.1NRT,290.0,40,D
            40.30,-120.30,330.0,1.0,1.0,2026-11-07,1929,T,MODIS,20,6.1NRT,290.0,900,D
            """;
        var cells = new Dictionary<(int, int), FireHotspotService.Cell>();
        await FireHotspotService.AddAsync(new StringReader(viirs), "NOAA-20 VIIRS", cells);
        await FireHotspotService.AddAsync(new StringReader(modis), "MODIS", cells);

        var features = FireHotspotService.ToFeatures(cells.Values);

        features.Should().HaveCount(2);
        var california = features[0];
        california.Should().Match<HazardFeature>(f => f.Kind == HazardKinds.Hotspot && f.Severity == "major"
            && f.Title == "Fire hotspots: 2 detections in the last day" && f.Detail.Contains("1,290 MW") && f.Detail.Contains("MODIS, NOAA-20 VIIRS")
            && f.ObservedAt == new DateTimeOffset(2026, 11, 7, 21, 5, 0, TimeSpan.Zero));
        features[1].Should().Match<HazardFeature>(f => f.Severity == "minor" && f.Title == "Fire hotspots: 2 detections in the last day"
            && f.Detail.StartsWith("20 MW"), "the low-confidence detection's 500 MW isn't counted");
        features[1].Url.Should().StartWith("https://firms.modaps.eosdis.nasa.gov/map/#d:24hrs;@111.93,-6.78");
    }

    [Fact]
    public void GdeltEvents_ShouldKeepCorroboratedConflict_AtANamedPlace_AsReported()
    {
        static string Row(string id, string root, string code, string quad, string sources, string articles, string geoType, string place,
            string lat, string lon, string url, string isRoot = "1")
        {
            var f = Enumerable.Repeat(string.Empty, 61).ToArray();
            (f[0], f[25], f[26], f[28], f[29], f[32], f[33]) = (id, isRoot, code, root, quad, sources, articles);
            (f[51], f[52], f[56], f[57], f[59], f[60]) = (geoType, place, lat, lon, "20261107211500", url);
            return string.Join('\t', f);
        }
        var tsv = string.Join('\n',
            Row("1", "19", "195", "4", "3", "12", "4", "Kharkiv, Kharkiv, Ukraine", "49.98", "36.25", "https://a.example/strike"),
            Row("2", "19", "195", "4", "1", "9", "4", "Kharkiv, Kharkiv, Ukraine", "49.99", "36.24", "https://b.example/strike"),
            Row("3", "18", "182", "4", "4", "20", "4", "Leeds, United Kingdom", "53.8", "-1.5", "https://c.example/assault"),
            Row("4", "19", "190", "4", "1", "2", "4", "Toronto, Ontario, Canada", "43.7", "-79.4", "https://d.example/thin"),
            Row("5", "20", "202", "4", "2", "6", "1", "Sudan", "15", "30", "https://e.example/country-only"),
            Row("6", "19", "193", "4", "2", "6", "4", "", "10", "10", "https://f.example/no-place"),
            Row("7", "19", "193", "4", "2", "6", "4", "Goma, Nord-Kivu, Congo", "-1.68", "29.23", "https://g.example/not-lead", isRoot: "0"),
            Row("8", "14", "141", "3", "5", "30", "4", "Paris, France", "48.86", "2.35", "https://h.example/protest"));

        var events = ConflictEventService.ParseGdeltExport(tsv);
        events.Select(e => e.Url).Should().Equal("https://a.example/strike", "https://b.example/strike");

        var pins = ConflictEventService.Reported(events);
        pins.Should().ContainSingle().Which.Should().Match<HazardFeature>(p => p.Kind == HazardKinds.Conflict
            && p.Title == "Air strike reported - Kharkiv, Kharkiv, Ukraine" && p.Severity == "major"
            && p.Detail.StartsWith("2 events in 21 articles") && p.Detail.Contains("not confirmed"));
        ConflictEventService.LatestStamp("123 abc http://data.gdeltproject.org/gdeltv2/20261107211500.export.CSV.zip\n")
            .Should().Be(new DateTimeOffset(2026, 11, 7, 21, 15, 0, TimeSpan.Zero));
    }

    [Fact]
    public void UcdpEvents_ShouldBeReadFromTheNewestMonthlyFile_AndAddedUpByArea()
    {
        const string page = """
            <a href="https://ucdp.uu.se/downloads/candidateged/GEDEvent_v26_0_8.csv">Aug</a>
            <a href="https://ucdp.uu.se/downloads/candidateged/GEDEvent_v26_0_9.csv">Sep</a>
            <a href="https://ucdp.uu.se/downloads/candidateged/GEDEvent_v26_01_26_09.csv">Quarterly</a>
            """;
        ConflictEventService.NewestCandidateFile(page).Should().Be("https://ucdp.uu.se/downloads/candidateged/GEDEvent_v26_0_9.csv");

        var csv = new StringBuilder();
        csv.AppendLine("id,type_of_violence,conflict_new_id,conflict_name,source_article,latitude,longitude,country,date_start,date_end,best");
        csv.AppendLine("1,1,333,Sudan: Government,\"RSF said, \"\"we hold the city\"\"\nsecond line\",13.18,30.22,Sudan,2026-09-01 00:00:00.000,2026-09-02 00:00:00.000,30");
        csv.AppendLine("2,1,333,Sudan: Government,src,13.20,30.30,Sudan,2026-09-20,2026-09-20,4");
        csv.AppendLine("3,3,400,One-sided: militia,src,13.10,30.45,Sudan,2026-09-25,2026-09-25,1");
        csv.AppendLine("4,2,500,Old fight,src,-3.0,29.0,Burundi,2026-04-01,2026-04-02,50");
        var events = ConflictEventService.ParseUcdp(new StringReader(csv.ToString()));
        events.Should().HaveCount(4, "a quoted field may hold commas, doubled quotes and line breaks");

        var areas = ConflictEventService.Confirmed(events);
        areas.Should().ContainSingle("the Burundi event ended more than 90 days before the newest one").Which.Should().Match<HazardFeature>(a =>
            a.Kind == HazardKinds.ConflictConfirmed && a.Title == "Sudan: Government" && a.Severity == "major"
            && a.Detail.StartsWith("35 deaths in 3 events, 01-09-26 to 25-09-26") && a.Detail.Contains("state-based, one-sided violence")
            && a.Url == "https://ucdp.uu.se/conflict/333");
    }

    [Fact]
    public void Humanitarian_ShouldPinEachNamedCountry_FromIfrc_SetApart()
    {
        const string ifrc = """
            {"results":[
              {"id":8146,"name":"HTI: Flood - 10-2026 - Haiti floods","dtype":{"name":"Flood"},"countries":[{"iso":"HT"}],
               "disaster_start_date":"2026-10-25T00:00:00Z","num_affected":59420,"ifrc_severity_level":1},
              {"id":8150,"name":"HTI: Epidemic - 11-2026 - Cholera","dtype":{"name":"Epidemic"},"countries":[{"iso":"HT"}],
               "disaster_start_date":"2026-11-02T00:00:00Z","num_affected":null,"ifrc_severity_level":2},
              {"id":7000,"name":"PRY: Old unrest","dtype":{"name":"Civil Unrest"},"countries":[{"iso":"PY"}],
               "disaster_start_date":"2026-06-01T00:00:00Z","num_affected":null,"ifrc_severity_level":0}
            ]}
            """;

        var pins = HumanitarianService.ParseIfrc(ifrc, Places, Now - HumanitarianService.IfrcWindow);

        pins.Should().HaveCount(2, "the June emergency is older than 60 days");
        pins[0].Should().Match<HazardFeature>(f => f.Kind == HazardKinds.Humanitarian && f.Title == "Flood - 10-2026 - Haiti floods"
            && f.Severity == "moderate" && f.Detail == "Flood · 59,420 people affected · IFRC GO" && f.Url == "https://go.ifrc.org/emergencies/8146"
            && f.Latitude == Places.Country("HT")!.Latitude);
        pins[1].Should().Match<HazardFeature>(f => f.Severity == "major" && f.Detail == "Epidemic · IFRC GO");
        HumanitarianService.SetApart(pins).Select(f => (f.Latitude, f.Longitude)).Distinct()
            .Should().HaveCount(2, "two emergencies in Haiti don't sit on one spot");
        HumanitarianService.Shorten(new string('a', 200), 110).Should().HaveLength(110).And.EndWith("…");
    }

    // World News (6g): what people looked up, beside a story.
    [Fact]
    public void WikipediaMostRead_ShouldNameTheArticlesAStorysHeadlinesMention()
    {
        const string json = """
            {"items":[{"articles":[
              {"article":"Main_Page","views":5000000,"rank":1},
              {"article":"Special:Search","views":900000,"rank":2},
              {"article":"Israel","views":300000,"rank":3},
              {"article":"2026_Israeli_legislative_election","views":250000,"rank":4},
              {"article":"Gadi_Eisenkot","views":210412,"rank":5},
              {"article":"Bundibugyo_virus_(disease)","views":120000,"rank":9},
              {"article":"Eisenkot_family","views":1000,"rank":40}
            ]}]}
            """;

        var articles = WikipediaMostReadService.Parse(json);

        articles.Select(a => a.Title).Should().Equal("Israel", "2026 Israeli legislative election", "Gadi Eisenkot", "Bundibugyo virus (disease)", "Eisenkot family");
        articles[2].Url.Should().Be("https://en.wikipedia.org/wiki/Gadi_Eisenkot");
        WikipediaMostReadService.Matching(["Israel's president taps Gadi Eisenkot after the Israeli legislative election", null], articles)
            .Select(a => a.Title).Should().Equal(["2026 Israeli legislative election", "Gadi Eisenkot"],
                "one-word titles would match every story about a place, and a leading year needn't be in the headline");
        WikipediaMostReadService.Matching(["Ebola disease caused by Bundibugyo virus, Democratic Republic of the Congo"], articles)
            .Should().ContainSingle().Which.Rank.Should().Be(9, "a bracketed note on the title isn't needed");
        WikipediaMostReadService.Matching(["Eisenkotting is not a word"], articles).Should().BeEmpty("only whole words match");
    }

    [Fact]
    public void Satellites_ShouldShowWhatEachOneSees_FromGibsTimes_AndTheIssCrew()
    {
        const string capabilities = """
            <Layer><ows:Identifier>MODIS_Terra_CorrectedReflectance_TrueColor</ows:Identifier><Dimension><Default>2026-11-08</Default></Dimension></Layer>
            <Layer><ows:Identifier>VIIRS_SNPP_CorrectedReflectance_TrueColor</ows:Identifier><Dimension><Default>2026-11-02</Default></Dimension></Layer>
            <Layer><ows:Identifier>GOES-East_ABI_GeoColor</ows:Identifier><Dimension><Default>2026-11-07T20:10:00Z</Default></Dimension></Layer>
            <Layer><ows:Identifier>NoDefault</ows:Identifier></Layer><Layer><ows:Identifier>Other</ows:Identifier><Default>x</Default></Layer>
            """;
        var times = SatelliteService.NewestTimes(capabilities,
            ["MODIS_Terra_CorrectedReflectance_TrueColor", "VIIRS_SNPP_CorrectedReflectance_TrueColor", "GOES-East_ABI_GeoColor", "NoDefault", "Missing"]);
        times.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["MODIS_Terra_CorrectedReflectance_TrueColor"] = "2026-11-08",
            ["VIIRS_SNPP_CorrectedReflectance_TrueColor"] = "2026-11-02",
            ["GOES-East_ABI_GeoColor"] = "2026-11-07T20:10:00Z"
        }, "a layer's time is read only from inside its own block");

        SatelliteService.DailyTime("2026-11-08", Now).Should().Be("2026-11-06", "the last whole day, not today's partial one");
        SatelliteService.DailyTime("2026-11-02", Now).Should().Be("2026-11-02", "a stopped satellite shows its last day");
        SatelliteService.DailyTime(null, Now).Should().Be("2026-11-06");
        SatelliteService.BelowUrl("VIIRS_NOAA20_CorrectedReflectance_TrueColor", "2026-11-06", 87, 178).Should()
            .Contain("LAYERS=VIIRS_NOAA20_CorrectedReflectance_TrueColor").And.Contain("BBOX=82,171,90,180").And.Contain("TIME=2026-11-06");

        SatelliteService.ParseCrew("""{"people":[{"name":"Anil Menon","iss":true},{"name":"Zhu Yangzhu","iss":false},{"name":"Anna Kikina","iss":true}]}""")
            .Should().Equal("Anil Menon", "Anna Kikina");
        SatelliteService.HasPublicData("25544").Should().BeTrue("the ISS");
        SatelliteService.HasPublicData("43013").Should().BeTrue("NOAA-20");
        SatelliteService.HasPublicData("48274").Should().BeFalse("China's station has no public data");
    }
}
