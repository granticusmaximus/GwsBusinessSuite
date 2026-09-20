using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Weather;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NwsAlertsServiceTests
{
    // Shaped directly from a live api.weather.gov/alerts/active response captured during
    // implementation - real field names, deliberately including a zone-only alert (geometry:
    // null) to exercise the "nothing to draw" skip path.
    private const string SampleJson = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "id": "urn:oid:polygon-alert",
              "type": "Feature",
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[-122.5, 47.5], [-122.0, 47.5], [-122.0, 48.0], [-122.5, 48.0], [-122.5, 47.5]]]
              },
              "properties": {
                "id": "urn:oid:polygon-alert",
                "event": "Severe Thunderstorm Warning",
                "severity": "Severe",
                "areaDesc": "King County, WA",
                "expires": "2026-09-20T20:00:00-07:00"
              }
            },
            {
              "id": "urn:oid:multipolygon-alert",
              "type": "Feature",
              "geometry": {
                "type": "MultiPolygon",
                "coordinates": [
                  [[[10.0, 45.0], [10.5, 45.0], [10.5, 45.5], [10.0, 45.5], [10.0, 45.0]]],
                  [[[11.0, 45.0], [11.5, 45.0], [11.5, 45.5], [11.0, 45.5], [11.0, 45.0]]]
                ]
              },
              "properties": {
                "id": "urn:oid:multipolygon-alert",
                "event": "Flood Watch",
                "severity": "Moderate",
                "areaDesc": "Far away land",
                "expires": "2026-09-21T00:00:00Z"
              }
            },
            {
              "id": "urn:oid:zone-only-alert",
              "type": "Feature",
              "geometry": null,
              "properties": {
                "id": "urn:oid:zone-only-alert",
                "event": "Winter Weather Advisory",
                "severity": "Minor",
                "areaDesc": "Zone-only area",
                "expires": "2026-09-22T00:00:00Z"
              }
            }
          ]
        }
        """;

    // Covers the Seattle-area polygon alert only.
    private static readonly BoundingBox SeattleBbox = new(North: 48.5, South: 47.0, East: -121.5, West: -123.0);

    [Fact]
    public async Task GetActiveAlertsAsync_ShouldReturnAlertsWithAVertexInsideTheBoundingBox()
    {
        var service = CreateService(_ => JsonResponse(SampleJson));

        var result = await service.GetActiveAlertsAsync(SeattleBbox);

        result.Should().ContainSingle();
        var alert = result[0];
        alert.Id.Should().Be("urn:oid:polygon-alert");
        alert.Event.Should().Be("Severe Thunderstorm Warning");
        alert.Severity.Should().Be("Severe");
        alert.Rings.Should().HaveCount(1);
        alert.Rings[0].Should().HaveCount(5);
    }

    [Fact]
    public async Task GetActiveAlertsAsync_ShouldParseEveryRingOfAMultiPolygon()
    {
        var farAwayBbox = new BoundingBox(North: 46, South: 44, East: 12, West: 9);
        var service = CreateService(_ => JsonResponse(SampleJson));

        var result = await service.GetActiveAlertsAsync(farAwayBbox);

        result.Should().ContainSingle(a => a.Id == "urn:oid:multipolygon-alert");
        result[0].Rings.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetActiveAlertsAsync_ShouldSkipAlertsWithNoGeometry()
    {
        var everythingBbox = new BoundingBox(90, -90, 180, -180);
        var service = CreateService(_ => JsonResponse(SampleJson));

        var result = await service.GetActiveAlertsAsync(everythingBbox);

        result.Select(a => a.Id).Should().NotContain("urn:oid:zone-only-alert");
    }

    [Fact]
    public async Task GetActiveAlertsAsync_ShouldCacheTheNationwideList_AcrossCallsWithDifferentBoundingBoxes()
    {
        var requestCount = 0;
        var service = CreateService(_ =>
        {
            requestCount++;
            return JsonResponse(SampleJson);
        });

        await service.GetActiveAlertsAsync(SeattleBbox);
        await service.GetActiveAlertsAsync(new BoundingBox(46, 44, 12, 9));

        requestCount.Should().Be(1, "the nationwide alert list should be cached rather than re-fetched per bounding box");
    }

    [Fact]
    public async Task GetActiveAlertsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await service.GetActiveAlertsAsync(SeattleBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/geo+json")
    };

    private static NwsAlertsService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory)) { BaseAddress = new Uri("https://api.weather.gov/") };
        return new NwsAlertsService(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<NwsAlertsService>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
