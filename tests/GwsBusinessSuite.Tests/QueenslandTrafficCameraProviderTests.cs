using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class QueenslandTrafficCameraProviderTests
{
    // Shaped from a live query against data.qldtraffic.qld.gov.au/webcameras.geojson during
    // implementation. GeoJSON coordinates are [longitude, latitude] order.
    private const string SampleJson = """
        {
          "features": [
            { "properties": { "id": 5001, "description": "Pacific Motorway at Eight Mile Plains", "image_url": "https://www.qldtraffic.qld.gov.au/cameras/5001.jpg" }, "geometry": { "coordinates": [153.084, -27.578] } },
            { "properties": { "id": 5002, "description": "No image", "image_url": "" }, "geometry": { "coordinates": [153.1, -27.6] } }
          ]
        }
        """;

    private static readonly BoundingBox QldBbox = new(North: -10, South: -29, East: 155, West: 138);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasUsingGeoJsonLonLatOrder()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(QldBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("qldtraffic-5001");
        camera.Name.Should().Be("QLD: Pacific Motorway at Eight Mile Plains");
        camera.Latitude.Should().Be(-27.578, "GeoJSON coordinates are [lon, lat], so latitude must come from index 1");
        camera.Longitude.Should().Be(153.084);
        camera.SourceName.Should().Be("QLD Traffic");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoImageUrl()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain("qldtraffic-5002");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(QldBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static QueenslandTrafficCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://data.qldtraffic.qld.gov.au/")
        };
        return new QueenslandTrafficCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<QueenslandTrafficCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
