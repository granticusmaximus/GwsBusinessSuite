using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.MessageSigns;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class MessageSignProviderTests
{
    // Shaped from live cwwp2.dot.ca.gov cmsStatusD01.json (2026-10-09).
    private const string CaltransJson = """
        {"data":[
          {"cms":{"index":"1","location":{"locationName":"S of Jct 36","nearbyPlace":"Fortuna","latitude":"40.6","longitude":"-124.15","direction":"South","route":"US-101"},"inService":"true",
            "message":{"display":"2 Pages (Extended)","phase1":{"phase1Line1":"HWY 36","phase1Line2":"1-WAY TRAFFIC","phase1Line3":"15 MILES AHEAD"},"phase2":{"phase2Line1":"EXPECT DELAYS","phase2Line2":"","phase2Line3":""}}}},
          {"cms":{"index":"2","location":{"locationName":"Blank sign","nearbyPlace":"","latitude":"40.7","longitude":"-124.1","direction":"North","route":"US-101"},"inService":"true",
            "message":{"display":"Blank","phase1":{"phase1Line1":"","phase1Line2":"","phase1Line3":""},"phase2":{"phase2Line1":"","phase2Line2":"","phase2Line3":""}}}},
          {"cms":{"index":"3","location":{"locationName":"Broken sign","nearbyPlace":"","latitude":"40.8","longitude":"-124.0","direction":"North","route":"US-101"},"inService":"false",
            "message":{"display":"Not Reported","phase1":{"phase1Line1":"Not Reported","phase1Line2":"Not Reported","phase1Line3":"Not Reported"},"phase2":{"phase2Line1":"Not Reported","phase2Line2":"Not Reported","phase2Line3":"Not Reported"}}}}
        ]}
        """;

    // Shaped from live mt.cdn.iteris-atis.com icons.dms.geojson (2026-10-09).
    private const string MontanaJson = """
        {"features":[
          {"geometry":{"coordinates":[-112.2638017,45.8489683],"type":"Point"},"properties":{"direction":"","id":"dms_15","mrm":"","name":"Butte - 36-043","report":"BRIDGE 4 MILES AHEAD<br><br>10 TON LIMIT<br><br>SINGLE LANE TRAFFIC","route":""}},
          {"geometry":{"coordinates":[-111.1,44.8],"type":"Point"},"properties":{"direction":"","id":"dms_99","mrm":"","name":"Dark sign","report":"","route":""}}
        ]}
        """;

    [Fact]
    public async Task Caltrans_ShouldKeepOnlyInServiceSignsShowingAMessage_WithEachPhaseAsAPage()
    {
        var provider = new CaltransMessageSignProvider(
            Client(request => request.RequestUri!.AbsolutePath.EndsWith("cmsStatusD01.json")
                ? Json(CaltransJson)
                : new HttpResponseMessage(HttpStatusCode.NotFound), "https://cwwp2.dot.ca.gov/"),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CaltransMessageSignProvider>.Instance);

        var signs = await provider.GetActiveSignsAsync();

        signs.Should().ContainSingle();
        var sign = signs[0];
        sign.Id.Should().Be("caltrans-1-1");
        sign.Name.Should().Be("S of Jct 36 (Fortuna)");
        sign.Road.Should().Be("US-101");
        sign.Direction.Should().Be("South");
        sign.Pages.Should().HaveCount(2);
        sign.Pages[0].Should().Equal("HWY 36", "1-WAY TRAFFIC", "15 MILES AHEAD");
        sign.Pages[1].Should().Equal("EXPECT DELAYS");
        sign.Text.Should().Be("HWY 36 1-WAY TRAFFIC 15 MILES AHEAD / EXPECT DELAYS");
    }

    [Fact]
    public async Task Iteris_ShouldSplitTheReportIntoLines_AndSkipDarkSigns()
    {
        var provider = new IterisMessageSignProvider(
            Client(_ => Json(MontanaJson)),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<IterisMessageSignProvider>.Instance,
            stateCode: "mt", sourceName: "511MT", sourceAttributionUrl: "https://www.511mt.net",
            coverage: new BoundingBox(49.1, 44.3, -104.0, -116.1));

        var signs = await provider.GetActiveSignsAsync();

        signs.Should().ContainSingle();
        signs[0].Pages.Should().ContainSingle().Which.Should().Equal("BRIDGE 4 MILES AHEAD", "10 TON LIMIT", "SINGLE LANE TRAFFIC");
        signs[0].Latitude.Should().Be(45.8489683);
        signs[0].Road.Should().BeNull();
    }

    [Fact]
    public async Task Directory_ShouldOnlyAskProvidersCoveringTheView_AndFilterToIt()
    {
        var california = new FakeProvider("CA", new BoundingBox(42.1, 32.4, -114.0, -124.5),
            [Sign("in-view", 34.05, -118.25), Sign("out-of-view", 40.6, -124.1)]);
        var montana = new FakeProvider("MT", new BoundingBox(49.1, 44.3, -104.0, -116.1), [Sign("mt", 46, -110)]);
        var directory = new MessageSignDirectoryService([california, montana], NullLogger<MessageSignDirectoryService>.Instance);

        var signs = await directory.GetSignsInBoundingBoxAsync(new BoundingBox(35, 33, -117, -119));

        signs.Select(s => s.Id).Should().Equal("in-view");
        montana.Calls.Should().Be(0);
    }

    private static MessageSign Sign(string id, double lat, double lon) =>
        new(id, id, lat, lon, null, null, [["TEST"]], "Test", "https://example.test");

    private sealed class FakeProvider(string name, BoundingBox coverage, IReadOnlyList<MessageSign> signs) : IMessageSignProvider
    {
        public int Calls { get; private set; }
        public string SourceName => name;
        public BoundingBox Coverage => coverage;

        public Task<IReadOnlyList<MessageSign>> GetActiveSignsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(signs);
        }
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond, string? baseAddress = null)
    {
        var client = new HttpClient(new RecordingHandler(respond));
        if (baseAddress is not null) client.BaseAddress = new Uri(baseAddress);
        return client;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
