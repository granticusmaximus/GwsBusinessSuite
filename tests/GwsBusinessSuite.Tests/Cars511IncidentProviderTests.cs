using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class Cars511IncidentProviderTests
{
    // Shaped from a live query against the CARS511_MN_Events_View FeatureServer layer during
    // implementation.
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "ID": "MN-1", "Route": "I-94", "headline": "Crash", "phrase": "Crash reported", "cause": "Crash blocking right lane.", "STYLE": "crash" }, "geometry": { "x": -93.26, "y": 44.98 } },
            { "attributes": { "ID": "MN-2", "Route": "I-35W", "headline": "Should be excluded", "phrase": "n/a", "cause": null, "STYLE": null }, "geometry": null }
          ]
        }
        """;

    private static readonly BoundingBox MinnesotaBbox = new(North: 49, South: 43, East: -89, West: -97);

    [Fact]
    public async Task GetIncidentsAsync_ShouldMapIncidentsAndUseTheConfiguredStateCodeAndAttribution()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson), stateCode: "MN", sourceName: "511MN", sourceAttributionUrl: "https://511mn.org");

        var result = await provider.GetIncidentsAsync(MinnesotaBbox);

        result.Should().ContainSingle("only the record with real geometry should survive");
        var incident = result[0];
        incident.Id.Should().Be("cars511-MN-MN-1");
        incident.RoadwayName.Should().Be("I-94");
        incident.Description.Should().Be("Crash blocking right lane.");
        incident.EventType.Should().Be("crash");
        incident.Latitude.Should().Be(44.98);
        incident.Longitude.Should().Be(-93.26);
        incident.SourceName.Should().Be("511MN");
        incident.SourceAttributionUrl.Should().Be("https://511mn.org");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldSkipRecordsWithNoGeometry()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson), stateCode: "MN", sourceName: "511MN", sourceAttributionUrl: "https://511mn.org");

        var result = await provider.GetIncidentsAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(i => i.Id).Should().NotContain("cars511-MN-MN-2");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldFallBackToPhraseAndHeadline_WhenStyleAndCauseAreMissing()
    {
        const string json = """
            { "features": [ { "attributes": { "ID": "NE-1", "Route": "I-80", "headline": "Roadwork", "phrase": "Lane restriction", "cause": null, "STYLE": null }, "geometry": { "x": -96.7, "y": 40.8 } } ] }
            """;
        var provider = CreateProvider(_ => JsonResponse(json), stateCode: "NE", sourceName: "511NE", sourceAttributionUrl: "https://511.nebraska.gov");

        var result = await provider.GetIncidentsAsync(new BoundingBox(90, -90, 180, -180));

        var incident = result.Single();
        incident.Id.Should().Be("cars511-NE-NE-1");
        incident.EventType.Should().Be("Lane restriction", "STYLE is missing, so EventType should fall back to phrase");
        incident.Description.Should().Be("Roadwork", "cause is missing, so Description should fall back to headline");
        incident.SourceName.Should().Be("511NE");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError), stateCode: "MN", sourceName: "511MN", sourceAttributionUrl: "https://511mn.org");

        var result = await provider.GetIncidentsAsync(MinnesotaBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static Cars511IncidentProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory, string stateCode, string sourceName, string sourceAttributionUrl)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services.arcgis.com/8lRhdTsQyJpO52F1/")
        };
        return new Cars511IncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<Cars511IncidentProvider>.Instance, stateCode, sourceName, sourceAttributionUrl);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
