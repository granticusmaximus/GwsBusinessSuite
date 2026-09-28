using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class HawaiiDotCameraProviderTests
{
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "OBJECTID": 1, "URL": "http://cctv.cdn.goakamai.org//SnapShot/320x240/TL-0024.jpg", "Camera_Description": "FARRINGTON & H-1 RAMPS" }, "geometry": { "x": -157.985, "y": 21.3959 } },
            { "attributes": { "OBJECTID": 2, "URL": "", "Camera_Description": "No image" }, "geometry": { "x": -157.9, "y": 21.3 } }
          ]
        }
        """;

    private static readonly BoundingBox OahuBbox = new(North: 21.7, South: 21.2, East: -157.6, West: -158.3);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCameras()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(OahuBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("hdot-1");
        camera.Name.Should().Be("HDOT: FARRINGTON & H-1 RAMPS");
        camera.StreamUrl.Should().Be("http://cctv.cdn.goakamai.org//SnapShot/320x240/TL-0024.jpg");
        camera.SourceName.Should().Be("HDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoUrl()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain("hdot-2");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(OahuBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static HawaiiDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services.arcgis.com/6I1ysurtNWNxkuwd/")
        };
        return new HawaiiDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<HawaiiDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
