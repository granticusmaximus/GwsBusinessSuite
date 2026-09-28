using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class MissouriDotCameraProviderTests
{
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "CAM_ID": 1000, "DESCRIPTION": "MO 13 and Norton", "URL2": "https://s2.ozarkstrafficoneview.com/rtplive/CAM01/playlist.m3u8", "STREAM_ERROR": "N" }, "geometry": { "x": -93.31, "y": 37.26 } },
            { "attributes": { "CAM_ID": 1001, "DESCRIPTION": "Broken stream", "URL2": "https://s2.ozarkstrafficoneview.com/rtplive/CAM02/playlist.m3u8", "STREAM_ERROR": "Y" }, "geometry": { "x": -93.25, "y": 37.21 } }
          ]
        }
        """;

    private static readonly BoundingBox MissouriBbox = new(North: 40, South: 36, East: -89, West: -95);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasUsingHlsStreamKind()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(MissouriBbox);

        result.Should().ContainSingle("the STREAM_ERROR=Y camera should be excluded");
        var camera = result[0];
        camera.Id.Should().Be("modot-1000");
        camera.Name.Should().Be("MoDOT: MO 13 and Norton");
        camera.StreamUrl.Should().Be("https://s2.ozarkstrafficoneview.com/rtplive/CAM01/playlist.m3u8");
        camera.StreamKind.Should().Be(CameraStreamKind.Hls);
        camera.SourceName.Should().Be("MoDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasFlaggedWithAStreamError()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain("modot-1001");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(MissouriBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static MissouriDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://mapping.modot.org/")
        };
        return new MissouriDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<MissouriDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
