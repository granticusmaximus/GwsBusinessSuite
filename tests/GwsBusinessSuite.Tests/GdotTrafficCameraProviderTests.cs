using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class GdotTrafficCameraProviderTests
{
    // Shaped from a live query against GDOT's real ArcGIS FeatureServer during implementation.
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "Id": "18549", "Latitude": 33.995518, "Longitude": -83.733475, "Url": "https://511ga.org/map/Cctv/18549", "Roadway": "SR 211" } },
            { "attributes": { "Id": "10651", "Latitude": 0, "Longitude": 0, "Url": "https://511ga.org/map/Cctv/10651", "Roadway": "I-20" } },
            { "attributes": { "Id": "99999", "Latitude": 34.0, "Longitude": -83.0, "Url": "", "Roadway": "No URL" } }
          ]
        }
        """;

    private static readonly BoundingBox GeorgiaBbox = new(North: 35, South: 33, East: -83, West: -84);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasWithRealCoordinatesAndAUrl()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(GeorgiaBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("gdot-18549");
        camera.Name.Should().Be("GDOT: SR 211");
        camera.Latitude.Should().Be(33.995518);
        camera.StreamUrl.Should().Be("https://511ga.org/map/Cctv/18549");
        camera.StreamKind.Should().Be(CameraStreamKind.Snapshot);
        camera.SourceName.Should().Be("GDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeNullIslandAndEmptyUrlCameras()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain(["gdot-10651", "gdot-99999"]);
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldCacheTheStatewideList_AcrossCallsWithDifferentBoundingBoxes()
    {
        var requestCount = 0;
        var provider = CreateProvider(_ =>
        {
            requestCount++;
            return JsonResponse(SampleJson);
        });

        await provider.GetCamerasAsync(GeorgiaBbox);
        await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        requestCount.Should().Be(1, "the statewide list should be cached rather than re-fetched per bounding box");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(GeorgiaBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static GdotTrafficCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services1.arcgis.com/2iUE8l8JKrP2tygQ/")
        };
        return new GdotTrafficCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<GdotTrafficCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
