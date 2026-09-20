using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class WindyWebcamProviderTests
{
    private const string SampleJson = """
        {
          "total": 2,
          "webcams": [
            {
              "id": "1234567890",
              "title": "Brussels Grand Place",
              "location": { "latitude": 50.8467, "longitude": 4.3525 },
              "images": { "current": { "preview": "https://webcams.windy.com/1234567890/current/preview.jpg" } }
            },
            {
              "id": "no-image-webcam",
              "title": "Missing image data",
              "location": { "latitude": 51.0, "longitude": 4.0 }
            }
          ]
        }
        """;

    private static readonly BoundingBox EuropeBbox = new(North: 55, South: 45, East: 10, West: -5);

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_WhenNoApiKeyIsConfigured()
    {
        var provider = CreateProvider(apiKey: "", _ => new HttpResponseMessage(HttpStatusCode.OK), out var requestCount);

        var result = await provider.GetCamerasAsync(EuropeBbox);

        result.Should().BeEmpty();
        requestCount().Should().Be(0, "an unconfigured provider must not make any HTTP call at all");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldMapWebcamsWithCompleteData()
    {
        var provider = CreateProvider("TEST_KEY", _ => JsonResponse(SampleJson), out _);

        var result = await provider.GetCamerasAsync(EuropeBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("windy-1234567890");
        camera.Name.Should().Be("Brussels Grand Place");
        camera.Latitude.Should().Be(50.8467);
        camera.Longitude.Should().Be(4.3525);
        camera.StreamUrl.Should().Be("https://webcams.windy.com/1234567890/current/preview.jpg");
        camera.StreamKind.Should().Be(CameraStreamKind.Snapshot);
        camera.SourceName.Should().Be("Windy");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldSkip_RatherThanThrow_ForAWebcamMissingImageData()
    {
        var provider = CreateProvider("TEST_KEY", _ => JsonResponse(SampleJson), out _);

        var result = await provider.GetCamerasAsync(EuropeBbox);

        result.Select(c => c.Id).Should().NotContain("windy-no-image-webcam");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldSendTheApiKeyHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider("TEST_KEY", request =>
        {
            capturedRequest = request;
            return JsonResponse(SampleJson);
        }, out _);

        await provider.GetCamerasAsync(EuropeBbox);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.GetValues("x-windy-api-key").Should().ContainSingle("TEST_KEY");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider("TEST_KEY", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError), out _);

        var result = await provider.GetCamerasAsync(EuropeBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static WindyWebcamProvider CreateProvider(
        string apiKey,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        out Func<int> requestCount)
    {
        var count = 0;
        var handler = new RecordingHandler(request =>
        {
            count++;
            return responseFactory(request);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.windy.com/") };
        requestCount = () => count;

        return new WindyWebcamProvider(
            http,
            Options.Create(new CameraIntelOptions { WindyApiKey = apiKey }),
            NullLogger<WindyWebcamProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
