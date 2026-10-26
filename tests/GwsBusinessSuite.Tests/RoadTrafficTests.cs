using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.RoadTraffic;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class RoadTrafficTests
{
    // About 4 km across, in downtown Atlanta (every road class).
    private static readonly BoundingBox Downtown = new(33.77, 33.74, -84.37, -84.40);

    private const string Catalog = """
        {"services":[
          {"name":"Hosted/HPMS_FULL_GA3_2023","type":"FeatureServer"},
          {"name":"Hosted/HPMS_FULL_GA_2024","type":"FeatureServer"},
          {"name":"Hosted/HPMS_FULL_GA_2025","type":"FeatureServer"},
          {"name":"Hosted/HPMS_FULL_GA_2025","type":"MapServer"},
          {"name":"Hosted/HPMS_FULL_NY_2025","type":"FeatureServer"},
          {"name":"Hosted/NTAD_Something","type":"FeatureServer"}
        ]}
        """;

    // Trimmed from the live HPMS_FULL_GA_2025 layer description (2026-10-25).
    private const string GeorgiaLayer = """
        {"name":"HPMS_FULL_GA_2025","objectIdField":"objectid",
         "extent":{"xmin":-85.61,"ymin":30.35,"xmax":-80.84,"ymax":35.0,"spatialReference":{"wkid":4326,"latestWkid":4326}},
         "fields":[{"name":"objectid"},{"name":"route_signing"},{"name":"route_number"},{"name":"route_qualifier"},{"name":"route_name"},{"name":"f_system"},
                   {"name":"facility_type"},{"name":"aadt"},{"name":"aadt_single_unit"},{"name":"aadt_combination"},{"name":"k_factor"},
                   {"name":"through_lanes"},{"name":"speed_limit"},{"name":"toll_id"}]}
        """;

    private const string NewYorkLayer = """
        {"name":"HPMS_FULL_NY_2025","objectIdField":"objectid",
         "extent":{"xmin":-79.76,"ymin":40.49,"xmax":-71.85,"ymax":45.01,"spatialReference":{"wkid":4326}},
         "fields":[{"name":"objectid"},{"name":"aadt"},{"name":"f_system"}]}
        """;

    // The I-75/85 Connector and an unsigned arterial, as the query answers them; one with no count.
    private const string Features = """
        {"exceededTransferLimit":false,"features":[
          {"attributes":{"objectid":11,"route_signing":2,"route_number":75,"route_qualifier":1,"route_name":"DOWNTOWN CONNECTOR","f_system":1,"facility_type":2,
                         "aadt":371000,"aadt_single_unit":11730,"aadt_combination":24520,"k_factor":8,"through_lanes":13,"speed_limit":55,"toll_id":null},
           "geometry":{"paths":[[[-84.3880,33.7500],[-84.3880,33.7600]]]}},
          {"attributes":{"objectid":12,"route_signing":1,"route_number":76215,"route_qualifier":1,"f_system":3,"facility_type":1,
                         "aadt":19800,"through_lanes":3,"speed_limit":999,"toll_id":"0"},
           "geometry":{"paths":[[[-84.3900,33.7550],[-84.3800,33.7550]]]}},
          {"attributes":{"objectid":13,"f_system":7,"aadt":0},"geometry":{"paths":[[[-84.39,33.75],[-84.38,33.75]]]}}
        ]}
        """;

    [Fact]
    public void ZoomFor_ShouldKeepBiggerRoadsOnlyAsTheViewWidens()
    {
        static BoundingBox Across(double km) => new(33.75 + km / 222.64, 33.75 - km / 222.64, -84.39 + km / 185, -84.39 - km / 185);

        HpmsRoadTrafficService.ZoomFor(Across(100)).Should().BeNull();
        HpmsRoadTrafficService.ZoomFor(Across(40))!.MaxFunctionalClass.Should().Be(3);
        HpmsRoadTrafficService.ZoomFor(Across(15))!.MaxFunctionalClass.Should().Be(5);
        HpmsRoadTrafficService.ZoomFor(Across(5))!.MaxFunctionalClass.Should().Be(7);
        HpmsRoadTrafficService.ViewWidthKm(Downtown).Should().BeInRange(3, 4);
    }

    [Fact]
    public void LatestServices_ShouldKeepTheNewestTwoFeatureServerReleasesPerState()
    {
        var services = HpmsRoadTrafficService.LatestServices(Catalog);

        services.Select(s => s.State).Should().Equal("GA", "NY");
        services[0].Years.Should().Equal(2025, 2024);
        services[1].Years.Should().Equal(2025);
    }

    [Theory]
    [InlineData(2, "75", 1, 1, 2, "I-75", true)]
    [InlineData(3, "129", 1, 3, 2, "US 129", true)]
    [InlineData(4, "247", 10, 3, 4, "SR 247 ramp", true)]
    [InlineData(3, "41", 3, 3, 2, "US 41 Business", true)]
    [InlineData(5, "75", 3, 3, 2, "I-75 Business", true)]
    [InlineData(1, "76215", 1, 3, 2, "Principal arterial", false)]
    [InlineData(4, null, 1, 4, 2, "Minor arterial", false)]
    public void RouteLabel_ShouldNameSignedRoutes_AndFallBackToTheRoadClass(int signing, string? number, int qualifier, int functionalClass, int facility,
        string expected, bool named)
    {
        HpmsRoadTrafficService.RouteLabel(signing, number, qualifier, functionalClass, facility).Should().Be((expected, named));
    }

    [Fact]
    public void RouteLabel_ShouldUseTheStatesNameForAnUnsignedRoad()
    {
        var name = HpmsRoadTrafficService.TitleName("GRAND CNTRL PKWY");

        name.Should().Be("Grand Cntrl Pkwy");
        HpmsRoadTrafficService.RouteLabel(1, "907", 1, 2, 2, name).Should().Be(("Grand Cntrl Pkwy", true));
        HpmsRoadTrafficService.RouteLabel(2, "678", 1, 1, 2, "Whitestone Expressway").Should().Be(("I-678", true), "a signed route keeps its number");
        HpmsRoadTrafficService.TitleName("  ").Should().BeNull();
    }

    [Fact]
    public void ParseFeatures_ShouldReadCountsAndRoadDetails_AndSkipUncountedSections()
    {
        var (roads, more) = HpmsRoadTrafficService.ParseFeatures(Features, "GA", 2025);

        more.Should().BeFalse();
        roads.Should().HaveCount(2);
        var connector = roads[0];
        connector.Label.Should().Be("I-75");
        connector.Name.Should().Be("Downtown Connector");
        connector.VehiclesPerDay.Should().Be(371000);
        connector.TrucksPerDay.Should().Be(36250);
        connector.PeakHourPercent.Should().Be(8);
        connector.Lanes.Should().Be(13);
        connector.SpeedLimitMph.Should().Be(55);
        connector.Toll.Should().BeFalse();
        connector.Paths.Should().ContainSingle().Which.Should().HaveCount(2);
        var arterial = roads[1];
        arterial.Named.Should().BeFalse();
        arterial.SpeedLimitMph.Should().BeNull("999 means no posted limit");
        arterial.OneWay.Should().BeTrue();
        arterial.Toll.Should().BeFalse("a toll id of 0 isn't a toll");
        arterial.TrucksPerDay.Should().BeNull();

        HpmsRoadTrafficService.ParseFeatures("""{"exceededTransferLimit":true,"features":[]}""", "GA", 2025).More.Should().BeTrue();
        var error = () => HpmsRoadTrafficService.ParseFeatures("""{"error":{"code":400,"message":"Invalid field"}}""", "GA", 2025);
        error.Should().Throw<HttpRequestException>().WithMessage("*Invalid field*");
    }

    [Fact]
    public void TilesFor_ShouldCoverTheViewOnTheTileGrid()
    {
        var one = HpmsRoadTrafficService.TilesFor(new BoundingBox(33.748, 33.742, -84.381, -84.392), 0.05).ToList();
        one.Should().ContainSingle().Which.Should().Be(new BoundingBox(33.75, 33.7, -84.35, -84.4));

        var two = HpmsRoadTrafficService.TilesFor(new BoundingBox(33.77, 33.74, -84.37, -84.39), 0.05).ToList();
        two.Should().HaveCount(2, "33.74-33.77 crosses the 33.75 row line and stays in one column");
        two.Should().OnlyContain(t => Math.Abs(t.North - t.South - 0.05) < 1e-9);
    }

    [Fact]
    public async Task GetAsync_ShouldLoadTheStateLayer_DeduplicateSectionsAcrossTiles_AndCacheThem()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/services/Hosted", StringComparison.Ordinal) => (HttpStatusCode.OK, Catalog),
            var path when path.EndsWith("HPMS_FULL_GA_2025/FeatureServer/0", StringComparison.Ordinal) => (HttpStatusCode.OK, GeorgiaLayer),
            var path when path.EndsWith("HPMS_FULL_NY_2025/FeatureServer/0", StringComparison.Ordinal) => (HttpStatusCode.OK, NewYorkLayer),
            var path when path.EndsWith("HPMS_FULL_GA_2025/FeatureServer/0/query", StringComparison.Ordinal) => (HttpStatusCode.OK, Features),
            _ => (HttpStatusCode.NotFound, "{}")
        });
        var service = new HpmsRoadTrafficService(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<HpmsRoadTrafficService>.Instance);

        var zoomedOut = await service.GetAsync(new BoundingBox(35, 32, -82, -86));
        zoomedOut.Note.Should().StartWith("Zoom in");
        handler.Urls.Should().BeEmpty();

        var result = await service.GetAsync(Downtown);
        result.Note.Should().BeNull();
        result.Roads.Select(r => r.Id).Should().BeEquivalentTo(new long[] { 11, 12 }, "every tile returns the same two sections");
        result.Roads.Last().Label.Should().Be("I-75", "the busiest road is drawn last, on top");
        result.Releases.Should().Equal("GA 2025");
        var query = handler.Urls.First(u => u.Contains("/query", StringComparison.Ordinal));
        query.Should().Contain("aadt%20%3E%200%20AND%20f_system%20%3C%3D%207").And.Contain("outSR=4326").And.Contain("objectid");
        query.Should().Contain("route_name").And.NotContain("future_aadt", "only fields the layer has are asked for");

        var calls = handler.Urls.Count;
        (await service.GetAsync(Downtown)).Roads.Should().HaveCount(2);
        handler.Urls.Should().HaveCount(calls, "the state list and both tiles are cached");

        (await service.GetAsync(new BoundingBox(51.52, 51.49, -0.10, -0.14))).Note.Should().Contain("cover the US");
    }

    [Fact]
    public async Task GetAsync_ShouldSaySo_WhenHpmsCantBeReached_AndTryGeorgias2023Name()
    {
        var down = new HpmsRoadTrafficService(new HttpClient(new RoutingHandler(_ => (HttpStatusCode.ServiceUnavailable, ""))),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<HpmsRoadTrafficService>.Instance);
        (await down.GetAsync(Downtown)).Note.Should().Contain("couldn't be reached");

        // Only the oddly named 2023 release answers: it's still found, and its own fields are used.
        const string onlyOld = """{"services":[{"name":"Hosted/HPMS_FULL_GA3_2023","type":"FeatureServer"}]}""";
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/services/Hosted", StringComparison.Ordinal) => (HttpStatusCode.OK, onlyOld),
            var path when path.EndsWith("HPMS_FULL_GA3_2023/FeatureServer/0", StringComparison.Ordinal) => (HttpStatusCode.OK, GeorgiaLayer),
            var path when path.EndsWith("HPMS_FULL_GA3_2023/FeatureServer/0/query", StringComparison.Ordinal) => (HttpStatusCode.OK, Features),
            _ => (HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""")
        });
        var old = new HpmsRoadTrafficService(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<HpmsRoadTrafficService>.Instance);
        (await old.GetAsync(Downtown)).Roads.Should().HaveCount(2);
    }

    [Fact]
    public void Summary_ShouldCountOnlyMilesInsideTheView_AndGroupSignedRoutes()
    {
        var (roads, _) = HpmsRoadTrafficService.ParseFeatures(Features, "GA", 2025);
        // A second I-75 section: still one road in the busiest list.
        var second = roads[0] with { Id = 99, VehiclesPerDay = 250000, TrucksPerDay = null, Paths = [[(-84.3880, 33.7600), (-84.3880, 33.7690)]] };
        // A busier ramp: it carries the freeway's traffic, so it isn't listed.
        var ramp = roads[1] with { Id = 77, Label = "Interstate ramp", Ramp = true, VehiclesPerDay = 60000, Paths = [[(-84.3950, 33.7450), (-84.3950, 33.7451)]] };
        var all = roads.Append(second).Append(ramp).ToList();

        var summary = RoadTrafficSummary.From(all, Downtown);

        summary.Sections.Should().Be(4);
        summary.Busiest.Select(b => b.Label).Should().Equal("I-75", "Principal arterial");
        summary.Busiest[0].VehiclesPerDay.Should().Be(371000);
        summary.Busiest[0].Name.Should().Be("Downtown Connector");
        summary.Busiest[0].TruckPercent.Should().Be(9.8);
        // 0.69 + 0.62 mi of I-75 and 0.58 mi of arterial, all inside the view.
        summary.MilesInView.Should().BeApproximately(1.89, 0.02);
        summary.VehicleMilesPerDay.Should().BeApproximately(371000 * 0.69 + 250000 * 0.62 + 19800 * 0.58 + 60000 * 0.007, 4000);
        summary.TruckPercent.Should().Be(9.8, "only sections with truck counts are in the truck share");

        var outside = new BoundingBox(33.752, 33.748, -84.380, -84.385);
        RoadTrafficSummary.MilesInside(roads[0], outside).Should().Be(0, "the connector runs west of that box");
    }

    // Trimmed from the live feed (2026-10-25): two readings for one link, a link with no speed, a cut-short point.
    private const string NycSpeeds = """
        [
          {"link_id":"4362342","speed":"13.04","travel_time":"378","data_as_of":"2026-10-25T19:02:06.000","link_name":"LIE E 84TH ST - 108TH ST","borough":"Queens",
           "link_points":"40.72966,-73.87775 40.730165,-73.87643 40.7304,-73.87567 40.7"},
          {"link_id":"4362342","speed":"9.5","travel_time":"500","data_as_of":"2026-10-25T18:47:06.000","link_name":"LIE E 84TH ST - 108TH ST","borough":"Queens",
           "link_points":"40.72966,-73.87775 40.730165,-73.87643"},
          {"link_id":"4616246","speed":"0","travel_time":"0","data_as_of":"2026-10-25T18:18:05.000","link_name":"BE N","borough":"Bronx",
           "link_points":"40.83,-73.84 40.84,-73.85"},
          {"link_id":"4763649","speed":"44.2","data_as_of":"2026-10-25T18:18:01.000","link_name":"VNB W","borough":"Brooklyn",
           "link_points":"40.6066,-74.0447 40.6080,-74.0400"}
        ]
        """;

    [Fact]
    public void NycParse_ShouldKeepTheNewestReadingPerLink_InNewYorkTime()
    {
        var links = NycDotSpeedProvider.Parse(NycSpeeds);

        links.Select(l => l.Id).Should().Equal("4362342", "4763649");
        var lie = links[0];
        lie.SpeedMph.Should().Be(13.04);
        lie.TravelTimeSeconds.Should().Be(378);
        lie.Path.Should().HaveCount(3, "the cut-short last pair is dropped");
        lie.Path[0].Should().Be((-73.87775, 40.72966));
        lie.AsOf.Should().Be(new DateTimeOffset(2026, 10, 25, 19, 2, 6, TimeSpan.FromHours(-4)));
        links[1].TravelTimeSeconds.Should().BeNull();
        links[1].SourceUrl.Should().Contain("i4gi-tjb9");
    }

    [Fact]
    public async Task NycProvider_ShouldAskForValidReadingsFromTheLastThreeHours_AndCacheThem()
    {
        var handler = new RoutingHandler(_ => (HttpStatusCode.OK, NycSpeeds));
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 26, 0, 30, 0, TimeSpan.Zero));
        var provider = new NycDotSpeedProvider(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), clock, NullLogger<NycDotSpeedProvider>.Instance);

        (await provider.GetAllAsync()).Should().HaveCount(2);
        (await provider.GetAllAsync()).Should().HaveCount(2);

        handler.Urls.Should().ContainSingle();
        var url = Uri.UnescapeDataString(handler.Urls[0]);
        url.Should().Contain("data_as_of > '2026-10-25T17:30:00'", "00:30 UTC is 20:30 in New York, less three hours");
        url.Should().Contain("status = '0'");
    }

    [Fact]
    public async Task LiveSpeedService_ShouldOnlyAskProvidersCoveringTheView_AndKeepLinksInIt()
    {
        var links = NycDotSpeedProvider.Parse(NycSpeeds);
        var provider = new FakeSpeedProvider(new BoundingBox(40.93, 40.49, -73.68, -74.27), links);
        var service = new LiveRoadSpeedService([provider], NullLogger<LiveRoadSpeedService>.Instance);

        (await service.GetAsync(Downtown)).Links.Should().BeEmpty();
        provider.Calls.Should().Be(0);

        var queens = await service.GetAsync(new BoundingBox(40.75, 40.70, -73.85, -73.90));
        queens.Links.Should().ContainSingle().Which.Name.Should().StartWith("LIE");
        queens.Sources.Should().Equal("NYC DOT");

        var failing = new LiveRoadSpeedService([new FakeSpeedProvider(provider.Coverage, null)], NullLogger<LiveRoadSpeedService>.Instance);
        (await failing.GetAsync(new BoundingBox(40.75, 40.70, -73.85, -73.90))).Note.Should().Contain("couldn't be reached");

        var summary = LiveSpeedSummary.From(links)!;
        summary.Links.Should().Be(2);
        summary.MedianMph.Should().Be(28.6);
        summary.SlowLinks.Should().Be(1);
        summary.Slowest[0].Id.Should().Be("4362342");
        LiveSpeedSummary.From([]).Should().BeNull();
    }

    private sealed class FakeSpeedProvider(BoundingBox coverage, IReadOnlyList<LiveRoadSpeed>? links) : ILiveRoadSpeedProvider
    {
        public int Calls { get; private set; }
        public string SourceName => "NYC DOT";
        public BoundingBox Coverage => coverage;

        public Task<IReadOnlyList<LiveRoadSpeed>> GetAllAsync(CancellationToken ct = default)
        {
            Calls++;
            return links is null ? throw new HttpRequestException("down") : Task.FromResult(links);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> route) : HttpMessageHandler
    {
        private readonly object _gate = new();
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate) Urls.Add(request.RequestUri!.AbsoluteUri);
            var (status, body) = route(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
