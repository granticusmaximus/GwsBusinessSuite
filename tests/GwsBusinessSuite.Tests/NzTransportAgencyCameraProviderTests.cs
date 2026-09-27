using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NzTransportAgencyCameraProviderTests
{
    // Shaped from a live query against NZTA's ArcGIS FeatureServer during implementation - every
    // key here is deliberately lowercase, matching this layer's own real field casing.
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "id": 101, "name": "SH1 Auckland Harbour Bridge", "offline": "false", "undermaintenance": "false", "imageurl": "https://www.trafficnz.info/camera/101.jpg" }, "geometry": { "x": 174.74, "y": -36.82 } },
            { "attributes": { "id": 102, "name": "Offline Camera", "offline": "true", "undermaintenance": "false", "imageurl": "https://www.trafficnz.info/camera/102.jpg" }, "geometry": { "x": 174.8, "y": -36.9 } },
            { "attributes": { "id": 103, "name": "Under Maintenance", "offline": "false", "undermaintenance": "true", "imageurl": "https://www.trafficnz.info/camera/103.jpg" }, "geometry": { "x": 174.9, "y": -37.0 } }
          ]
        }
        """;

    private static readonly BoundingBox NzBbox = new(North: -30, South: -45, East: 179, West: 166);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasFromLowercaseFields()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(NzBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("nzta-101");
        camera.Name.Should().Be("NZTA: SH1 Auckland Harbour Bridge");
        camera.Latitude.Should().Be(-36.82);
        camera.Longitude.Should().Be(174.74);
        camera.StreamUrl.Should().Be("https://www.trafficnz.info/camera/101.jpg");
        camera.SourceName.Should().Be("NZTA");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeOfflineAndUnderMaintenanceCameras()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain(["nzta-102", "nzta-103"]);
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(NzBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static NzTransportAgencyCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services.arcgis.com/XTtANUDT8Va4DLwI/")
        };
        return new NzTransportAgencyCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<NzTransportAgencyCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
