using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class TennesseeDotIncidentProviderTests
{
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "OBJECTID": 588752, "CD_ROAD_NAMES": null, "DESCRIPTION": "State Route 347 WB in Sullivan County - Scheduled Road Work.", "EVENT_TYPE": "Operations", "HAS_CLOSURE": 1 }, "geometry": { "x": -82.5887, "y": 36.4663 } }
          ]
        }
        """;

    private static readonly BoundingBox TennesseeBbox = new(North: 37, South: 34, East: -81, West: -90);

    [Fact]
    public async Task GetIncidentsAsync_ShouldMapClosureSeverity_WhenHasClosureIsOne()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetIncidentsAsync(TennesseeBbox);

        result.Should().ContainSingle();
        var incident = result[0];
        incident.Id.Should().Be("tdot-event-588752");
        incident.RoadwayName.Should().Be("Unknown roadway", "CD_ROAD_NAMES was null on the real live sample");
        incident.Description.Should().Be("State Route 347 WB in Sullivan County - Scheduled Road Work.");
        incident.EventType.Should().Be("Operations");
        incident.Severity.Should().Be("closure");
        incident.SourceName.Should().Be("TDOT SmartWay");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldMapUnknownSeverity_WhenHasClosureIsZero()
    {
        const string json = """
            { "features": [ { "attributes": { "OBJECTID": 1, "CD_ROAD_NAMES": null, "DESCRIPTION": "d", "EVENT_TYPE": "Operations", "HAS_CLOSURE": 0 }, "geometry": { "x": -82, "y": 36 } } ] }
            """;
        var provider = CreateProvider(_ => JsonResponse(json));

        var result = await provider.GetIncidentsAsync(new BoundingBox(90, -90, 180, -180));

        result.Single().Severity.Should().Be("unknown");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetIncidentsAsync(TennesseeBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static TennesseeDotIncidentProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://spatial.tdot.tn.gov/")
        };
        return new TennesseeDotIncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<TennesseeDotIncidentProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
