using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class MapillaryImageryProviderTests
{
    private const string SampleJson = """
        {
          "data": [
            {
              "id": "123456789",
              "thumb_256_url": "https://scontent.mapillary.com/123456789/thumb256.jpg",
              "geometry": { "type": "Point", "coordinates": [4.3525, 50.8467] }
            },
            {
              "id": "no-geometry-image",
              "thumb_256_url": "https://scontent.mapillary.com/no-geometry-image/thumb256.jpg"
            }
          ]
        }
        """;

    private static readonly BoundingBox EuropeBbox = new(North: 55, South: 45, East: 10, West: -5);

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_WhenNoAccessTokenIsConfigured()
    {
        var provider = CreateProvider(accessToken: "", _ => new HttpResponseMessage(HttpStatusCode.OK), out var requestCount);

        var result = await provider.GetCamerasAsync(EuropeBbox);

        result.Should().BeEmpty();
        requestCount().Should().Be(0, "an unconfigured provider must not make any HTTP call at all");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldMapImagesWithCompleteData()
    {
        var provider = CreateProvider("TEST_TOKEN", _ => JsonResponse(SampleJson), out _);

        var result = await provider.GetCamerasAsync(EuropeBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("mapillary-123456789");
        camera.Latitude.Should().Be(50.8467);
        camera.Longitude.Should().Be(4.3525);
        camera.StreamUrl.Should().Be("https://scontent.mapillary.com/123456789/thumb256.jpg");
        camera.StreamKind.Should().Be(CameraStreamKind.Snapshot);
        camera.SourceName.Should().Be("Mapillary");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldSkip_RatherThanThrow_ForAnImageMissingGeometry()
    {
        var provider = CreateProvider("TEST_TOKEN", _ => JsonResponse(SampleJson), out _);

        var result = await provider.GetCamerasAsync(EuropeBbox);

        result.Select(c => c.Id).Should().NotContain("mapillary-no-geometry-image");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldSendTheAccessTokenAsAQueryParameter()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider("TEST_TOKEN", request =>
        {
            capturedRequest = request;
            return JsonResponse(SampleJson);
        }, out _);

        await provider.GetCamerasAsync(EuropeBbox);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.Query.Should().Contain("access_token=TEST_TOKEN");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider("TEST_TOKEN", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError), out _);

        var result = await provider.GetCamerasAsync(EuropeBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static MapillaryImageryProvider CreateProvider(
        string accessToken,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        out Func<int> requestCount)
    {
        var count = 0;
        var handler = new RecordingHandler(request =>
        {
            count++;
            return responseFactory(request);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.mapillary.com/") };
        requestCount = () => count;

        return new MapillaryImageryProvider(
            http,
            Options.Create(new CameraIntelOptions { MapillaryAccessToken = accessToken }),
            NullLogger<MapillaryImageryProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
