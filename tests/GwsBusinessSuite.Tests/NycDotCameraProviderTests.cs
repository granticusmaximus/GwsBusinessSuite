using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NycDotCameraProviderTests
{
    // Shaped from a live query against webcams.nyctmc.org during implementation - field names
    // are deliberately camelCase here, matching the API's own real casing.
    private const string SampleJson = """
        [
          { "id": "8a6bc417-4877-4ebe-8052-88c1b261baf1", "name": "Central Park West @ 86 St", "latitude": 40.785302, "longitude": -73.969353, "area": "Manhattan", "isOnline": "true", "imageUrl": "https://webcams.nyctmc.org/api/cameras/8a6bc417/image" },
          { "id": "offline-cam", "name": "Offline Camera", "latitude": 40.7, "longitude": -74.0, "area": "Manhattan", "isOnline": "false", "imageUrl": "https://webcams.nyctmc.org/api/cameras/offline-cam/image" },
          { "id": "null-island", "name": "Bad Coords", "latitude": 0, "longitude": 0, "area": "Manhattan", "isOnline": "true", "imageUrl": "https://webcams.nyctmc.org/api/cameras/null-island/image" }
        ]
        """;

    private static readonly BoundingBox NycBbox = new(North: 41, South: 40, East: -73, West: -75);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasFromCamelCaseFields()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(NycBbox);

        result.Should().ContainSingle("only the online camera with real coordinates should survive");
        var camera = result[0];
        camera.Id.Should().Be("nycdot-8a6bc417-4877-4ebe-8052-88c1b261baf1");
        camera.Name.Should().Be("NYC DOT: Central Park West @ 86 St");
        camera.Latitude.Should().Be(40.785302);
        camera.Longitude.Should().Be(-73.969353);
        camera.StreamUrl.Should().Be("https://webcams.nyctmc.org/api/cameras/8a6bc417/image");
        camera.SourceName.Should().Be("NYC DOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeOfflineAndNullIslandCameras()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain(["nycdot-offline-cam", "nycdot-null-island"]);
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldCacheTheCityWideList_AcrossCallsWithDifferentBoundingBoxes()
    {
        var requestCount = 0;
        var provider = CreateProvider(_ =>
        {
            requestCount++;
            return JsonResponse(SampleJson);
        });

        await provider.GetCamerasAsync(NycBbox);
        await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        requestCount.Should().Be(1, "the city-wide list should be cached rather than re-fetched per bounding box");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(NycBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static NycDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://webcams.nyctmc.org/")
        };
        return new NycDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<NycDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
