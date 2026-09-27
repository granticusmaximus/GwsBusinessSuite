using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class QueenslandTrafficIncidentProviderTests
{
    // Shaped from a live query against data.qldtraffic.qld.gov.au/events_v2.geojson during
    // implementation - this feed genuinely mixes geometry types across records (MultiLineString
    // for a road-segment closure, MultiPoint for a point hazard, GeometryCollection wrapping
    // either), which is exactly what the recursive TryExtractFirstPoint helper exists to handle.
    private const string SampleJson = """
        {
          "features": [
            { "properties": { "id": 9001, "event_type": "crash", "event_subtype": "Crash", "event_priority": "3 - Medium Impact", "description": "Multi-vehicle crash.", "road_summary": { "road_name": "Bruce Highway" } },
              "geometry": { "type": "MultiLineString", "coordinates": [[[152.9, -27.1], [152.95, -27.15]]] } },
            { "properties": { "id": 9002, "event_type": "hazard", "event_subtype": "Debris", "event_priority": "1 - Low Impact", "description": "", "road_summary": { "road_name": "Ipswich Motorway" } },
              "geometry": { "type": "MultiPoint", "coordinates": [[152.8, -27.2]] } },
            { "properties": { "id": 9003, "event_type": "roadwork", "event_subtype": "Planned works", "event_priority": "2 - Medium Impact", "description": "Lane closure.", "road_summary": { "road_name": "Gateway Motorway" } },
              "geometry": { "type": "GeometryCollection", "geometries": [ { "type": "Point", "coordinates": [153.05, -27.4] } ] } },
            { "properties": { "id": 9004, "event_type": "unknown", "event_subtype": "Unresolvable", "event_priority": "1 - Low Impact", "description": "No usable geometry.", "road_summary": null },
              "geometry": { "type": "GeometryCollection", "geometries": [] } }
          ]
        }
        """;

    private static readonly BoundingBox QldBbox = new(North: -10, South: -29, East: 155, West: 138);

    [Fact]
    public async Task GetIncidentsAsync_ShouldExtractPointFromMultiLineStringGeometry()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetIncidentsAsync(QldBbox);

        var incident = result.Single(i => i.Id == "qldtraffic-event-9001");
        incident.Latitude.Should().Be(-27.1);
        incident.Longitude.Should().Be(152.9);
        incident.RoadwayName.Should().Be("Bruce Highway");
        incident.EventType.Should().Be("crash");
        incident.Severity.Should().Be("3 - Medium Impact");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldExtractPointFromMultiPointGeometry()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetIncidentsAsync(QldBbox);

        var incident = result.Single(i => i.Id == "qldtraffic-event-9002");
        incident.Latitude.Should().Be(-27.2);
        incident.Longitude.Should().Be(152.8);
        incident.Description.Should().Be("Debris", "an empty description should fall back to the event subtype");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldExtractPointFromGeometryCollection()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetIncidentsAsync(QldBbox);

        var incident = result.Single(i => i.Id == "qldtraffic-event-9003");
        incident.Latitude.Should().Be(-27.4);
        incident.Longitude.Should().Be(153.05);
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldSkipRecordsWithNoExtractablePoint()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetIncidentsAsync(QldBbox);

        result.Select(i => i.Id).Should().NotContain("qldtraffic-event-9004");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetIncidentsAsync(QldBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static QueenslandTrafficIncidentProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://data.qldtraffic.qld.gov.au/")
        };
        return new QueenslandTrafficIncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<QueenslandTrafficIncidentProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
