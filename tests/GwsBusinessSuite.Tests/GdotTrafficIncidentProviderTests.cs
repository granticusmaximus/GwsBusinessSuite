using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class GdotTrafficIncidentProviderTests
{
    // Shaped from a live query against GDOT's real GDOT_511_Events_Public_View FeatureServer
    // during implementation. The "ID" key is deliberately all-caps here, matching the field's
    // real casing confirmed live - a real, confirmed gotcha: this layer's own field name differs
    // from GDOT's camera layer (which uses "Id"), and System.Text.Json's default
    // GetFromJsonAsync options are case-sensitive, so this regression-guards the
    // [JsonPropertyName("ID")] attribute on GdotEventAttributes.
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "ID": 501, "RoadwayName": "SR 101", "Description": "Crash blocking left lane.", "EventType": "accidentsAndIncidents", "Severity": "minor", "Latitude": 34.256856, "Longitude": -85.178232 } },
            { "attributes": { "ID": 502, "RoadwayName": "Null Island Rd", "Description": "Should be excluded.", "EventType": "roadwork", "Severity": "minor", "Latitude": 0, "Longitude": 0 } }
          ]
        }
        """;

    private static readonly BoundingBox GeorgiaBbox = new(North: 35, South: 33, East: -83, West: -86);

    [Fact]
    public async Task GetIncidentsAsync_ShouldMapIncidentsWithRealCoordinates()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetIncidentsAsync(GeorgiaBbox);

        result.Should().ContainSingle();
        var incident = result[0];
        incident.Id.Should().Be("gdot-event-501", "the real ArcGIS field is \"ID\" (all-caps) - this fails if the JsonPropertyName binding regresses");
        incident.RoadwayName.Should().Be("SR 101");
        incident.EventType.Should().Be("accidentsAndIncidents");
        incident.Latitude.Should().Be(34.256856);
        incident.SourceName.Should().Be("GDOT");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldExcludeNullIslandIncidents()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetIncidentsAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(i => i.Id).Should().NotContain("gdot-event-502");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldCacheTheStatewideList_AcrossCallsWithDifferentBoundingBoxes()
    {
        var requestCount = 0;
        var provider = CreateProvider(_ =>
        {
            requestCount++;
            return JsonResponse(SampleJson);
        });

        await provider.GetIncidentsAsync(GeorgiaBbox);
        await provider.GetIncidentsAsync(new BoundingBox(90, -90, 180, -180));

        requestCount.Should().Be(1, "the statewide list should be cached rather than re-fetched per bounding box");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetIncidentsAsync(GeorgiaBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static GdotTrafficIncidentProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services1.arcgis.com/2iUE8l8JKrP2tygQ/")
        };
        return new GdotTrafficIncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<GdotTrafficIncidentProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
