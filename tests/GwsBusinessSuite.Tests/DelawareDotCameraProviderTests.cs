using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class DelawareDotCameraProviderTests
{
    // Shaped from a live query against DelDOT's real FirstMap ArcGIS FeatureServer.
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "ID": "KCAM001", "TITLE": "DE 1 @ MILFORD NECK ROAD (NORTH OFF)", "M3U8S": "https://video.deldot.gov:443/live/KCAM001.stream/playlist.m3u8" }, "geometry": { "x": -75.448625, "y": 38.990931 } },
            { "attributes": { "ID": "KCAM002", "TITLE": "No stream", "M3U8S": "" }, "geometry": { "x": -75.5, "y": 39.1 } }
          ]
        }
        """;

    private static readonly BoundingBox DelawareBbox = new(North: 40, South: 38, East: -74, West: -76);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasUsingHlsStreamKind()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(DelawareBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("deldot-KCAM001");
        camera.Name.Should().Be("DelDOT: DE 1 @ MILFORD NECK ROAD (NORTH OFF)");
        camera.StreamUrl.Should().Be("https://video.deldot.gov:443/live/KCAM001.stream/playlist.m3u8");
        camera.StreamKind.Should().Be(CameraStreamKind.Hls, "this layer only publishes stream URLs, no static image field");
        camera.SourceName.Should().Be("DelDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoStreamUrl()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain("deldot-KCAM002");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(DelawareBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static DelawareDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://enterprise.firstmaptest.delaware.gov/")
        };
        return new DelawareDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<DelawareDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
