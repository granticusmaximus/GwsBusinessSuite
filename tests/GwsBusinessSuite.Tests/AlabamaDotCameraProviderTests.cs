using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class AlabamaDotCameraProviderTests
{
    // Shaped from a live query against api.algotraffic.com/v4.0/cameras - the top-level response
    // is a plain JSON array, not wrapped in an object.
    private const string SampleJson = """
        [
          { "id": 1845, "location": { "latitude": 30.5351, "longitude": -88.2395, "displayRouteDesignator": "I-10", "displayCrossStreet": "McDonald Rd" }, "accessLevel": "Public", "snapshotImageUrl": "https://api.algotraffic.com/v4/Cameras/1845/snapshot.jpg" },
          { "id": 1846, "location": { "latitude": 30.6, "longitude": -88.3, "displayRouteDesignator": "I-65", "displayCrossStreet": "" }, "accessLevel": "FirstResponder", "snapshotImageUrl": "https://api.algotraffic.com/v4/Cameras/1846/snapshot.jpg" }
        ]
        """;

    private static readonly BoundingBox AlabamaBbox = new(North: 35, South: 30, East: -85, West: -89);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapOnlyPublicCameras()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(AlabamaBbox);

        result.Should().ContainSingle("the FirstResponder-only camera should be excluded");
        var camera = result[0];
        camera.Id.Should().Be("aldot-1845");
        camera.Name.Should().Be("ALDOT: I-10 - McDonald Rd");
        camera.StreamUrl.Should().Be("https://api.algotraffic.com/v4/Cameras/1845/snapshot.jpg");
        camera.SourceName.Should().Be("ALDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(AlabamaBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static AlabamaDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://api.algotraffic.com/")
        };
        return new AlabamaDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<AlabamaDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
