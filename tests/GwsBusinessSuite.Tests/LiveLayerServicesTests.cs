using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.LiveLayers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class LiveLayerServicesTests
{
    private static readonly BoundingBox Atlanta = new(34.0, 33.5, -84.0, -84.7);

    // Trimmed from a live OpenSky answer (2026-10-12): one airborne, one on the ground, one with no position.
    private const string States = """
        {"time":1791808361,"states":[
          ["ad9db8","DAL3173 ","United States",1791808361,1791808361,-84.1532,33.605,4411.98,false,192.44,45.32,7.48,null,4655.82,null,false,0],
          ["ab1667","VXP393  ","United States",1791808357,1791808357,-84.4412,33.6432,null,true,0,0,null,null,null,"7013",false,0],
          ["a00001","NOPOS   ","United States",1791808357,1791808357,null,null,1000,false,100,90,0,null,null,null,false,0]
        ]}
        """;

    [Fact]
    public void ParseStates_ShouldKeepAirborneAircraft_InFeetAndKnots()
    {
        var plane = AircraftService.ParseStates(States).Should().ContainSingle().Subject;

        plane.Callsign.Should().Be("DAL3173");
        plane.AltitudeFeet.Should().BeApproximately(14475, 1);
        plane.SpeedKnots.Should().BeApproximately(374, 1);
        plane.HeadingDegrees.Should().Be(45.32);
        plane.VerticalRateFeetPerMinute.Should().BeApproximately(1472, 1);
        AircraftService.ParseStates("""{"time":1,"states":null}""").Should().BeEmpty();
    }

    [Fact]
    public async Task Aircraft_ShouldNeedAZoomedInView_CacheAMinute_AndBackOffWhenRateLimited()
    {
        var handler = new Handler(HttpStatusCode.OK, States);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AircraftService(new HttpClient(handler), cache, NullLogger<AircraftService>.Instance);

        (await service.GetAsync(new BoundingBox(40, 30, -80, -90))).Note.Should().StartWith("Zoom in");
        handler.Calls.Should().Be(0);

        (await service.GetAsync(Atlanta)).Aircraft.Should().ContainSingle();
        (await service.GetAsync(Atlanta)).Aircraft.Should().ContainSingle();
        handler.Calls.Should().Be(1, "the second look within a minute is served from cache");
        handler.LastUrl.Should().Contain("lamin=33.5&lomin=-85&lamax=34&lomax=-84");

        var limited = new AircraftService(new HttpClient(new Handler(HttpStatusCode.TooManyRequests, "")), new MemoryCache(new MemoryCacheOptions()), NullLogger<AircraftService>.Instance);
        (await limited.GetAsync(Atlanta)).Note.Should().Contain("allowance is used up");
        (await limited.GetAsync(new BoundingBox(40, 39.5, -100, -100.5))).Note.Should().Contain("allowance is used up", "it backs off everywhere, not just that area");
    }

    [Fact]
    public void AirQuality_ShouldParseOneOrManyLocations_AndBandTheAqi()
    {
        const string many = """
            [{"latitude":33.8,"longitude":-84.4,"current":{"time":"2026-10-12T12:00","us_aqi":31,"pm2_5":4.7,"ozone":62.0}},
             {"latitude":34.0,"longitude":-84.0,"current":{"time":"2026-10-12T12:00","us_aqi":null}},
             {"latitude":34.2,"longitude":-84.2,"current":{"time":"2026-10-12T12:00","us_aqi":157.4}}]
            """;
        const string one = """{"latitude":33.8,"longitude":-84.4,"current":{"us_aqi":72}}""";

        AirQualityService.Parse(many).Select(r => (r.UsAqi, r.Category)).Should().Equal((31, "Good"), (157, "Unhealthy"));
        AirQualityService.Parse(many)[0].Pm25.Should().Be(4.7);
        AirQualityService.Parse(one).Should().ContainSingle().Which.Category.Should().Be("Moderate");
        AirQualityService.Category(120).Should().Be("Unhealthy for sensitive groups");
        AirQualityService.Category(350).Should().Be("Hazardous");
    }

    [Fact]
    public async Task AirQuality_ShouldSampleAGridOverTheView_InOneRequest()
    {
        var handler = new Handler(HttpStatusCode.OK, """[{"latitude":33.6,"longitude":-84.6,"current":{"us_aqi":40}}]""");
        var service = new AirQualityService(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<AirQualityService>.Instance);

        AirQualityService.Grid(new BoundingBox(40, 30, -80, -90)).Should().HaveCount(AirQualityService.Rows * AirQualityService.Columns);
        (await service.GetAsync(new BoundingBox(40, 30, -80, -90))).Readings.Should().ContainSingle();
        await service.GetAsync(new BoundingBox(40, 30, -80, -90));
        handler.Calls.Should().Be(1);
        handler.LastUrl.Should().Contain("current=us_aqi,pm2_5,ozone").And.Contain("latitude=31.2,31.2,");
        (await service.GetAsync(new BoundingBox(80, -80, 170, -170))).Note.Should().StartWith("Zoom in");
    }

    // Shaped like a live ODIN /odi/map?scale=COUNTY answer (2026-10-12), trimmed.
    private const string Odin = """
        [{"countyName":"Rabun","id":"13241","metersOut":413,"outageType":"COUNTY","stateName":"Georgia",
          "feature":{"type":"Polygon","coordinates":[[[-83.6,34.7],[-83.1,34.7],[-83.1,35.0],[-83.6,35.0],[-83.6,34.7]]]},
          "outages":[{"utilityName":"HAYWOOD ELECTRIC MEMBER CORP","metersOut":413},{"utilityName":"HAYWOOD ELECTRIC MEMBER CORP","metersOut":0}]},
         {"countyName":"Macon","id":"37113","metersOut":2076,"outageType":"COUNTY","stateName":"North Carolina",
          "feature":{"type":"MultiPolygon","coordinates":[[[[-83.7,35.0],[-83.1,35.0],[-83.1,35.3],[-83.7,35.0]]],[[[-83.0,35.1],[-82.9,35.1],[-82.9,35.2],[-83.0,35.1]]]]},
          "outages":[]},
         {"countyName":"Nowhere","id":"1","metersOut":0,"stateName":"Georgia","feature":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}},
         {"countyName":null,"id":"13","metersOut":5,"stateName":"Georgia","feature":null}]
        """;

    [Fact]
    public void PowerOutages_ShouldParseCountiesMostAffectedFirst_SkippingEmptyOnes()
    {
        var areas = PowerOutageService.Parse(Odin);

        areas.Select(a => (a.Name, a.CustomersOut)).Should().Equal(("Macon County", 2076), ("Rabun County", 413));
        areas[0].Rings.Should().HaveCount(2, "each polygon of a MultiPolygon is drawn");
        areas[1].Utilities.Should().Equal("HAYWOOD ELECTRIC MEMBER CORP");
        areas[1].Latitude.Should().BeApproximately(34.85, 0.001);
        areas[1].Longitude.Should().BeApproximately(-83.35, 0.001);
    }

    [Fact]
    public async Task PowerOutages_ShouldAskForCountiesWhenZoomedIn_AndStatesWhenNot()
    {
        var handler = new Handler(HttpStatusCode.OK, Odin);
        var service = new PowerOutageService(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<PowerOutageService>.Instance);

        (await service.GetAsync(Atlanta)).Areas.Should().HaveCount(2);
        handler.LastUrl.Should().EndWith("minx=-85&miny=33&maxx=-84&maxy=34&scale=COUNTY");
        await service.GetAsync(new BoundingBox(50, 25, -65, -125));
        handler.LastUrl.Should().EndWith("scale=STATE");
        await service.GetAsync(Atlanta);
        handler.Calls.Should().Be(2, "the county view is cached");
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string LastUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
