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
