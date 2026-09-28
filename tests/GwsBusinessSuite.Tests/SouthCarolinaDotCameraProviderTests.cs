using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class SouthCarolinaDotCameraProviderTests
{
    // Shaped from a live query against sc.cdn.iteris-atis.com's real GeoJSON feed - active/
    // problem_stream are real JSON booleans here (confirmed live), not ArcGIS-style strings.
    private const string SampleJson = """
        {
          "total_streams": 3,
          "features": [
            { "properties": { "id": "2735", "description": "I-77 S @ MM 4.9", "image_url": "https://scdotsnap.us-east-1.skyvdn.com/thumbs/10002.flv.png", "active": true, "problem_stream": false }, "geometry": { "coordinates": [-80.997286, 33.948503] } },
            { "properties": { "id": "2736", "description": "Inactive", "image_url": "https://scdotsnap.us-east-1.skyvdn.com/thumbs/10003.flv.png", "active": false, "problem_stream": false }, "geometry": { "coordinates": [-81.0, 33.9] } },
            { "properties": { "id": "2737", "description": "Problem stream", "image_url": "https://scdotsnap.us-east-1.skyvdn.com/thumbs/10004.flv.png", "active": true, "problem_stream": true }, "geometry": { "coordinates": [-81.1, 34.0] } }
          ]
        }
        """;

    private static readonly BoundingBox ScBbox = new(North: 35, South: 32, East: -78, West: -83);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapActiveNonProblemCameras()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(ScBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("scdot-2735");
        camera.Name.Should().Be("SCDOT: I-77 S @ MM 4.9");
        camera.Latitude.Should().Be(33.948503, "GeoJSON coordinates are [lon, lat]");
        camera.Longitude.Should().Be(-80.997286);
        camera.SourceName.Should().Be("SCDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeInactiveAndProblemStreamCameras()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain(["scdot-2736", "scdot-2737"]);
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(ScBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static SouthCarolinaDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://sc.cdn.iteris-atis.com/")
        };
        return new SouthCarolinaDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<SouthCarolinaDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
