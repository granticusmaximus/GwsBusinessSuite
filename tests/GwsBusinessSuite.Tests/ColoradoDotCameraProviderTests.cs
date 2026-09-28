using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class ColoradoDotCameraProviderTests
{
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "CameraId": 10111, "CameraName": "Colfax Ave @ Wadsworth Blvd", "URL_Cam": "http://www.cotrip.org/images/camera?imageURL=60095E.jpg" }, "geometry": { "x": -105.0817, "y": 39.74 } },
            { "attributes": { "CameraId": 10194, "CameraName": "No image", "URL_Cam": "" }, "geometry": { "x": -104.98, "y": 39.85 } }
          ]
        }
        """;

    private static readonly BoundingBox ColoradoBbox = new(North: 41, South: 37, East: -102, West: -109);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCameras()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(ColoradoBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("cdot-10111");
        camera.Name.Should().Be("CDOT: Colfax Ave @ Wadsworth Blvd");
        camera.StreamUrl.Should().Be("http://www.cotrip.org/images/camera?imageURL=60095E.jpg");
        camera.SourceName.Should().Be("CDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoImageUrl()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain("cdot-10194");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(ColoradoBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static ColoradoDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services.arcgis.com/DO4gTjwJVIJ7O9Ca/")
        };
        return new ColoradoDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<ColoradoDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
