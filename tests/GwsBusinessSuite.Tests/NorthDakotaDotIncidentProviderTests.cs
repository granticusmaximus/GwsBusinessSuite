using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NorthDakotaDotIncidentProviderTests
{
    // The real response is a plain top-level JSON array of Feature-shaped objects, not a
    // FeatureCollection wrapper - confirmed live, this test regression-guards that shape.
    private const string SampleJson = """
        [
          { "id": "39330", "geometry": { "coordinates": [-97.74951, 46.91886] }, "properties": { "HwyDesc": "I 94", "Comment": "Oriska Rest Area is closed for an extended improvement project.", "ConditionDesc": "Warning", "DelayTimeDesc": "No Delays" } }
        ]
        """;

    private static readonly BoundingBox NorthDakotaBbox = new(North: 49, South: 45, East: -96, West: -104.5);

    [Fact]
    public async Task GetIncidentsAsync_ShouldParseThePlainTopLevelArrayShape()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetIncidentsAsync(NorthDakotaBbox);

        result.Should().ContainSingle();
        var incident = result[0];
        incident.Id.Should().Be("nddot-event-39330");
        incident.RoadwayName.Should().Be("I 94");
        incident.Description.Should().Be("Oriska Rest Area is closed for an extended improvement project.");
        incident.EventType.Should().Be("Warning");
        incident.Severity.Should().Be("No Delays");
        incident.SourceName.Should().Be("NDDOT");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetIncidentsAsync(NorthDakotaBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static NorthDakotaDotIncidentProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://travelfiles.dot.nd.gov/")
        };
        return new NorthDakotaDotIncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<NorthDakotaDotIncidentProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
