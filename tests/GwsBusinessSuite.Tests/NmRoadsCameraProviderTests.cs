using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NmRoadsCameraProviderTests
{
    // Shaped from live servicev5.nmroads.com/RealMapWAR/GetCameraInfo (2026-10-09).
    private const string SampleJson = """
        {"cameraInfo":[
          {"sdpFileMedRes":"","mobile":false,"videoServer":"rtmp://video.nmroads.com/nmroads","lon":-106.244,"title":"I-25 @ Lower La Bajada","sdpFileHighRes":"i25_lowerlabajada.stream","grouping":"Santa Fe Area","enabled":true,"snapshotFile":"http://ss.nmroads.com/snapshots/i25_lowerlabajada.jpg","stream":true,"district":5,"name":"I-25@La_Bajada_Lower","lat":35.506001},
          {"mobile":true,"lon":-106.6,"title":"Mobile unit","enabled":true,"name":"Mobile_1","lat":35.1},
          {"mobile":false,"lon":-106.6,"title":"Disabled","enabled":false,"name":"Disabled_1","lat":35.1}
        ]}
        """;

    [Fact]
    public async Task GetCamerasAsync_ShouldMapEnabledFixedCameras_ToTheHttpsImageEndpoint()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleJson, System.Text.Encoding.UTF8, "application/json")
        });

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("nmroads-I-25@La_Bajada_Lower");
        camera.Name.Should().Be("NMRoads: I-25 @ Lower La Bajada");
        camera.Latitude.Should().Be(35.506001);
        camera.Longitude.Should().Be(-106.244);
        camera.StreamKind.Should().Be(CameraStreamKind.Snapshot);
        camera.StreamUrl.Should().Be("https://servicev5.nmroads.com/RealMapWAR/GetCameraImage?ts=0&cameraName=I-25%40La_Bajada_Lower",
            "the list's own snapshotFile is http-only, which an https page can't load");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Should().BeEmpty();
    }

    private static NmRoadsCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        new(
            new HttpClient(new RecordingHandler(responseFactory)) { BaseAddress = new Uri("https://servicev5.nmroads.com/") },
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<NmRoadsCameraProvider>.Instance);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
