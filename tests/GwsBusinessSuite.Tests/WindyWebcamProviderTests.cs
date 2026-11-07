using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class WindyWebcamProviderTests
{
    // Windy's v3 shape (the OpenAPI schema in its docs page, checked 07-11-26): a numeric webcamId,
    // a status, and images.current.preview.
    private const string SampleJson = """
        {
          "total": 4,
          "webcams": [
            {
              "webcamId": 1234567890,
              "title": "Brussels Grand Place",
              "status": "active",
              "location": { "latitude": 50.8467, "longitude": 4.3525, "city": "Brussels" },
              "images": { "current": { "icon": "https://imgproxy.windy.com/i.jpg", "preview": "https://webcams.windy.com/1234567890/current/preview.jpg" }, "daylight": {}, "sizes": {} }
            },
            {
              "webcamId": 222,
              "title": "Missing image data",
              "status": "active",
              "location": { "latitude": 51.0, "longitude": 4.0 }
            },
            {
              "webcamId": 333,
              "title": "Switched off",
              "status": "inactive",
              "location": { "latitude": 51.0, "longitude": 4.0 },
              "images": { "current": { "preview": "https://webcams.windy.com/333/current/preview.jpg" } }
            },
            {
              "webcamId": "not-an-object-location",
              "location": "somewhere",
              "images": { "current": { "preview": 7 } }
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

        result.Select(c => c.Id).Should().Equal("windy-1234567890");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldAskForTheViewAsOneBbox_MostViewedFirst_AndReuseTheAnswerBriefly()
    {
        var requests = new List<string>();
        var provider = CreateProvider("TEST_KEY", request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return JsonResponse(SampleJson);
        }, out _);

        await provider.GetCamerasAsync(new BoundingBox(North: 51.123456, South: 50.5, East: 4.75, West: 3.999));
        await provider.GetCamerasAsync(new BoundingBox(North: 51.12, South: 50.5, East: 4.75, West: 4.0));

        requests.Should().ContainSingle("a view the same to two decimals reuses the answer");
        requests[0].Should().Be("/webcams/api/v3/webcams?bbox=51.12,4.75,50.5,4&include=location,images&limit=50&sortKey=popularity&sortDirection=desc");

        await provider.GetCamerasAsync(new BoundingBox(North: 95, South: -95, East: -170, West: 170));
        requests[1].Should().StartWith("/webcams/api/v3/webcams?bbox=90,180,-90,-180&", "Windy's box can't wrap the antimeridian or pass the poles");
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
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<WindyWebcamProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
