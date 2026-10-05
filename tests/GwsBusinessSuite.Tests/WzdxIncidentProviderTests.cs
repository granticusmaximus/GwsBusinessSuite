using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class WzdxIncidentProviderTests
{
    // Shaped from a live query against Utah's real WZDx v4 feed - geometry is commonly a
    // LineString (a work zone spans a road segment), confirmed live, not a single Point.
    private const string SampleJson = """
        {
          "features": [
            {
              "id": "2365_eastbound",
              "properties": {
                "core_details": { "event_type": "work-zone", "road_names": ["I-80"], "description": "UDOT will improve I-80 between 1300 East and 2300 East." },
                "road_event_id": "2365_eastbound",
                "vehicle_impact": "some-lanes-closed"
              },
              "geometry": { "type": "LineString", "coordinates": [[-111.855022, 40.719556], [-111.836362, 40.717367]] }
            },
            {
              "id": "no-geometry",
              "properties": { "core_details": { "event_type": "work-zone", "road_names": ["I-15"], "description": "d" }, "vehicle_impact": "unknown" },
              "geometry": { "type": "LineString", "coordinates": [] }
            }
          ]
        }
        """;

    private static readonly BoundingBox UtahBbox = new(North: 42, South: 37, East: -109, West: -114);

    [Fact]
    public async Task GetIncidentsAsync_ShouldExtractTheFirstPointFromALineStringGeometry()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson), feedUrl: "https://udottraffic.utah.gov/wzdx/udot/v40/data",
            sourceKey: "ut", sourceName: "UDOT WZDx", sourceAttributionUrl: "https://udottraffic.utah.gov");

        var result = await provider.GetIncidentsAsync(UtahBbox);

        result.Should().ContainSingle("the record with empty coordinates should be skipped");
        var incident = result[0];
        incident.Id.Should().Be("wzdx-ut-2365_eastbound");
        incident.RoadwayName.Should().Be("I-80");
        incident.Description.Should().Be("UDOT will improve I-80 between 1300 East and 2300 East.");
        incident.EventType.Should().Be("work-zone");
        incident.Severity.Should().Be("some-lanes-closed");
        incident.Latitude.Should().Be(40.719556);
        incident.Longitude.Should().Be(-111.855022);
        incident.SourceName.Should().Be("UDOT WZDx");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError), feedUrl: "https://az511.gov/api/wzdx",
            sourceKey: "az", sourceName: "AZ511 WZDx", sourceAttributionUrl: "https://az511.gov");

        var result = await provider.GetIncidentsAsync(UtahBbox);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldOnlyKeepWorkHappeningNow()
    {
        // Shaped from Florida's live feed (2026-10-04): statewide feeds mix active, planned
        // ("pending") and finished ("completed") work zones, with ISO start/end dates.
        const string json = """
            {"features":[
              {"id":"active","properties":{"core_details":{"event_type":"work-zone","road_names":["I-4"]},"start_date":"2026-09-01T00:00:00Z","end_date":"2026-12-01T00:00:00Z","event_status":"active"},"geometry":{"type":"MultiPoint","coordinates":[[-81.4,28.5]]}},
              {"id":"future","properties":{"core_details":{"event_type":"work-zone","road_names":["I-4"]},"start_date":"2026-11-01T00:00:00Z","end_date":"2026-12-01T00:00:00Z","event_status":"pending"},"geometry":{"type":"MultiPoint","coordinates":[[-81.4,28.5]]}},
              {"id":"finished","properties":{"core_details":{"event_type":"work-zone","road_names":["I-4"]},"start_date":"2026-08-01T00:00:00Z","end_date":"2026-09-01T00:00:00Z"},"geometry":{"type":"MultiPoint","coordinates":[[-81.4,28.5]]}},
              {"id":"completed-early","properties":{"core_details":{"event_type":"work-zone","road_names":["I-4"]},"start_date":"2026-09-01T00:00:00Z","end_date":"2026-12-01T00:00:00Z","event_status":"completed"},"geometry":{"type":"MultiPoint","coordinates":[[-81.4,28.5]]}},
              {"id":"open-ended","properties":{"core_details":{"event_type":"work-zone","road_names":["I-95"]},"start_date":"2026-01-01T00:00:00Z"},"geometry":{"type":"Point","coordinates":[-81.6,30.3]}}
            ]}
            """;
        var http = new HttpClient(new RecordingHandler(_ => JsonResponse(json)));
        var provider = new WzdxIncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<WzdxIncidentProvider>.Instance,
            "https://example.test/wzdx", "fl", "FL511 WZDx", "https://fl511.com", new FixedClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)));

        var result = await provider.GetIncidentsAsync(new BoundingBox(North: 31, South: 24, East: -80, West: -88));

        result.Select(incident => incident.Id).Should().Equal("wzdx-fl-active", "wzdx-fl-open-ended");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static WzdxIncidentProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory, string feedUrl, string sourceKey, string sourceName, string sourceAttributionUrl)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory));
        return new WzdxIncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<WzdxIncidentProvider>.Instance,
            feedUrl, sourceKey, sourceName, sourceAttributionUrl);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
