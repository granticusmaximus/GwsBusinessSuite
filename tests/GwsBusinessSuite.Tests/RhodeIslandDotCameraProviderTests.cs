using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class RhodeIslandDotCameraProviderTests
{
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "OBJECTID": 1, "Description": "95 42.4 N CAM - Broadway (Pawt)", "CCVEWebURL": "https://www.dot.ri.gov/img/travel/camimages/95_42.4_N_CAM - Broadway (Pawt).jpg" }, "geometry": { "x": -71.36, "y": 41.83 } },
            { "attributes": { "OBJECTID": 2, "Description": "No feed", "CCVEWebURL": "" }, "geometry": { "x": -71.4, "y": 41.9 } }
          ]
        }
        """;

    private static readonly BoundingBox RhodeIslandBbox = new(North: 42.1, South: 41.1, East: -71, West: -71.9);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasIncludingSpacesAndParensInTheUrl()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(RhodeIslandBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("ridot-1");
        camera.Name.Should().Be("RIDOT: 95 42.4 N CAM - Broadway (Pawt)");
        camera.StreamUrl.Should().Be("https://www.dot.ri.gov/img/travel/camimages/95_42.4_N_CAM - Broadway (Pawt).jpg");
        camera.SourceName.Should().Be("RIDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoUrl()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain("ridot-2");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(RhodeIslandBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static RhodeIslandDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://risegis.ri.gov/")
        };
        return new RhodeIslandDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<RhodeIslandDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
