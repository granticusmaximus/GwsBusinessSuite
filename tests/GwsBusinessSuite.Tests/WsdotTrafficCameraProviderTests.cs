using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class WsdotTrafficCameraProviderTests
{
    private const string SampleJson = """
        [
          { "CameraID": 1001, "Title": "I-5 at Mercer St", "DisplayLatitude": 47.6205, "DisplayLongitude": -122.3325,
            "ImageURL": "https://images.wsdot.wa.gov/nw/001001.jpg", "IsActive": true },
          { "CameraID": 1002, "Title": "Inactive Camera", "DisplayLatitude": 47.5, "DisplayLongitude": -122.5,
            "ImageURL": "https://images.wsdot.wa.gov/nw/001002.jpg", "IsActive": false },
          { "CameraID": 1003, "Title": "No Image Camera", "DisplayLatitude": 47.6, "DisplayLongitude": -122.4,
            "ImageURL": "", "IsActive": true },
          { "CameraID": 1004, "Title": "Far Away Camera", "DisplayLatitude": 10.0, "DisplayLongitude": 10.0,
            "ImageURL": "https://images.wsdot.wa.gov/nw/001004.jpg", "IsActive": true }
        ]
        """;

    // Seattle-ish, deliberately excludes the "Far Away Camera" at (10, 10).
    private static readonly BoundingBox SeattleBbox = new(North: 48, South: 47, East: -122, West: -123);

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_WhenNoAccessCodeIsConfigured()
    {
        var provider = CreateProvider(accessCode: "", _ => new HttpResponseMessage(HttpStatusCode.OK), out var requestCount);

        var result = await provider.GetCamerasAsync(SeattleBbox);

        result.Should().BeEmpty();
        requestCount().Should().Be(0, "an unconfigured provider must not make any HTTP call at all");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldMapActiveCamerasWithImages_WithinTheBoundingBox()
    {
        var provider = CreateProvider("TEST_CODE", _ => JsonResponse(SampleJson), out _);

        var result = await provider.GetCamerasAsync(SeattleBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("wsdot-1001");
        camera.Name.Should().Be("I-5 at Mercer St");
        camera.Latitude.Should().Be(47.6205);
        camera.Longitude.Should().Be(-122.3325);
        camera.StreamUrl.Should().Be("https://images.wsdot.wa.gov/nw/001001.jpg");
        camera.StreamKind.Should().Be(CameraStreamKind.Snapshot);
        camera.SourceName.Should().Be("WSDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeInactiveCameras_CamerasWithNoImage_AndCamerasOutsideTheBoundingBox()
    {
        var provider = CreateProvider("TEST_CODE", _ => JsonResponse(SampleJson), out _);

        var result = await provider.GetCamerasAsync(SeattleBbox);

        result.Select(c => c.Id).Should().NotContain(["wsdot-1002", "wsdot-1003", "wsdot-1004"]);
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldCacheTheFullCameraList_AcrossCallsWithDifferentBoundingBoxes()
    {
        var provider = CreateProvider("TEST_CODE", _ => JsonResponse(SampleJson), out var requestCount);

        await provider.GetCamerasAsync(SeattleBbox);
        await provider.GetCamerasAsync(new BoundingBox(North: 11, South: 9, East: 11, West: 9));

        requestCount().Should().Be(1, "the statewide list should be cached rather than re-fetched per bounding box");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider("TEST_CODE", _ => throw new HttpRequestException("boom"), out _);

        var result = await provider.GetCamerasAsync(SeattleBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static WsdotTrafficCameraProvider CreateProvider(
        string accessCode,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        out Func<int> requestCount)
    {
        var count = 0;
        var handler = new RecordingHandler(request =>
        {
            count++;
            return responseFactory(request);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://wsdot.wa.gov/Traffic/api/") };
        requestCount = () => count;

        return new WsdotTrafficCameraProvider(
            http,
            Options.Create(new CameraIntelOptions { WsdotAccessCode = accessCode }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<WsdotTrafficCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
