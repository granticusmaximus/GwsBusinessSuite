using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class MissouriDotIncidentProviderTests
{
    // This layer's schema is genuinely thinner than most other incident providers - no Route, no
    // StartTime/EndTime, no Priority field exists at all, confirmed live.
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "OBJECTID": 1, "STYLE": "roadwork", "MESSAGE": "Lane closure on MO 13.", "HEADLINE": "Roadwork" }, "geometry": { "x": -93.31, "y": 37.26 } }
          ]
        }
        """;

    private static readonly BoundingBox MissouriBbox = new(North: 40, South: 36, East: -89, West: -95);

    [Fact]
    public async Task GetIncidentsAsync_ShouldMapIncidents_WithRoadwayNameAlwaysUnknown()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetIncidentsAsync(MissouriBbox);

        result.Should().ContainSingle();
        var incident = result[0];
        incident.Id.Should().Be("modot-event-1");
        incident.RoadwayName.Should().Be("Unknown roadway", "this layer genuinely has no roadway field");
        incident.Description.Should().Be("Lane closure on MO 13.");
        incident.EventType.Should().Be("roadwork");
        incident.SourceName.Should().Be("MoDOT");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldFallBackToHeadline_WhenMessageIsMissing()
    {
        const string json = """
            { "features": [ { "attributes": { "OBJECTID": 2, "STYLE": null, "MESSAGE": null, "HEADLINE": "Crash" }, "geometry": { "x": -93.3, "y": 37.2 } } ] }
            """;
        var provider = CreateProvider(_ => JsonResponse(json));

        var result = await provider.GetIncidentsAsync(new BoundingBox(90, -90, 180, -180));

        var incident = result.Single();
        incident.Description.Should().Be("Crash");
        incident.EventType.Should().Be("unknown");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetIncidentsAsync(MissouriBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static MissouriDotIncidentProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://mapping.modot.org/")
        };
        return new MissouriDotIncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<MissouriDotIncidentProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
