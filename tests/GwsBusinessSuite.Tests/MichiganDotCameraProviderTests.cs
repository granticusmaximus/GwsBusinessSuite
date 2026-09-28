using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class MichiganDotCameraProviderTests
{
    // Location values carry their own leading space in the real feed (" @ Mound NB"), designed to
    // concatenate directly after Route - confirmed live.
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "OBJECTID": 1, "Route": "11 Mile", "Location": " @ Mound NB", "Image": "https://micamerasimages.net/thumbs/semtoc_cam_253.flv.jpg?item=1" }, "geometry": { "x": -83.0448, "y": 42.4913 } },
            { "attributes": { "OBJECTID": 2, "Route": "No image", "Location": "", "Image": "" }, "geometry": { "x": -83.0, "y": 42.5 } }
          ]
        }
        """;

    private static readonly BoundingBox MichiganBbox = new(North: 45, South: 41, East: -82, West: -90);

    [Fact]
    public async Task GetCamerasAsync_ShouldCombineRouteAndLocationIntoOneName()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(MichiganBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("mdot-mi-1");
        camera.Name.Should().Be("MDOT: 11 Mile @ Mound NB");
        camera.StreamUrl.Should().Be("https://micamerasimages.net/thumbs/semtoc_cam_253.flv.jpg?item=1");
        camera.SourceName.Should().Be("MDOT MiDrive");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoImage()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain("mdot-mi-2");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(MichiganBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static MichiganDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services2.arcgis.com/67lKNkQ2TO1I3lhR/")
        };
        return new MichiganDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<MichiganDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
