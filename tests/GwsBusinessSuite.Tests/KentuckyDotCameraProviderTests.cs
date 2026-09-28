using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class KentuckyDotCameraProviderTests
{
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "OBJECTID": 1, "description": "I-65 just South of I-265 Exit 6 Indiana", "snapshot": "http://pws.trafficwise.org/pullover/172_65_56_11.jpg" }, "geometry": { "x": -85.7535, "y": 38.3452 } },
            { "attributes": { "OBJECTID": 2, "description": "No snapshot", "snapshot": "" }, "geometry": { "x": -85.7, "y": 38.3 } }
          ]
        }
        """;

    private static readonly BoundingBox KentuckyBbox = new(North: 39, South: 37, East: -82, West: -89);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasUsingTheDescriptionField()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(KentuckyBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("kytc-1");
        camera.Name.Should().Be("KYTC: I-65 just South of I-265 Exit 6 Indiana");
        camera.StreamUrl.Should().Be("http://pws.trafficwise.org/pullover/172_65_56_11.jpg");
        camera.SourceName.Should().Be("KYTC");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoSnapshot()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain("kytc-2");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(KentuckyBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static KentuckyDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://kygisserver.ky.gov/")
        };
        return new KentuckyDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<KentuckyDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
