using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class KartaViewProviderTests
{
    private const string SampleJson = """
        {
          "status": { "apiCode": 600, "apiMessage": "OK", "httpCode": 200, "httpMessage": "Success" },
          "result": {
            "data": [
              {
                "id": "719329",
                "lat": "44.426384",
                "lng": "26.104135",
                "fileurlTh": "https://storage2.openstreetcam.org/files/photo/2016/4/18/th/2385_71ad1_5714a34ebc8b1.jpg"
              },
              {
                "id": "no-thumb-photo",
                "lat": "44.5",
                "lng": "26.2"
              }
            ]
          }
        }
        """;

    // A tight bbox around a single point, so the derived query radius is small but non-zero.
    private static readonly BoundingBox TightBbox = new(North: 44.4269, South: 44.4267, East: 26.1026, West: 26.1024);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapPhotosWithCompleteData()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson), out _);

        var result = await provider.GetCamerasAsync(TightBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("kartaview-719329");
        camera.Latitude.Should().Be(44.426384);
        camera.Longitude.Should().Be(26.104135);
        camera.StreamUrl.Should().Be("https://storage2.openstreetcam.org/files/photo/2016/4/18/th/2385_71ad1_5714a34ebc8b1.jpg");
        camera.StreamKind.Should().Be(CameraStreamKind.Snapshot);
        camera.SourceName.Should().Be("KartaView");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldSkip_RatherThanThrow_ForAPhotoMissingAThumbnail()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson), out _);

        var result = await provider.GetCamerasAsync(TightBbox);

        result.Select(c => c.Id).Should().NotContain("kartaview-no-thumb-photo");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError), out _);

        var result = await provider.GetCamerasAsync(TightBbox);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldNotCallTheApi_WhenTheDerivedRadiusIsEffectivelyZero()
    {
        var degenerateBbox = new BoundingBox(North: 44.42, South: 44.42, East: 26.1, West: 26.1);
        var provider = CreateProvider(_ => JsonResponse(SampleJson), out var requestCount);

        await provider.GetCamerasAsync(degenerateBbox);

        requestCount().Should().Be(0);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static KartaViewProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        out Func<int> requestCount)
    {
        var count = 0;
        var handler = new RecordingHandler(request =>
        {
            count++;
            return responseFactory(request);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openstreetcam.org/") };
        requestCount = () => count;

        return new KartaViewProvider(http, NullLogger<KartaViewProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
