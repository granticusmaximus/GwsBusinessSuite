using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class MarylandDotCameraProviderTests
{
    private const string SampleJson = """
        {
          "features": [
            { "attributes": { "OBJECTID": 1, "location": "I-95 at MD 43", "url": "https://chart.maryland.gov/video/video.php?feed=101" }, "geometry": { "x": -76.5, "y": 39.3 } },
            { "attributes": { "OBJECTID": 2, "location": "No feed", "url": "" }, "geometry": { "x": -76.6, "y": 39.4 } }
          ]
        }
        """;

    private static readonly BoundingBox MarylandBbox = new(North: 40, South: 38, East: -75, West: -78);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasUsingTheDirectChartFeedUrl()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(MarylandBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("mdot-1");
        camera.Name.Should().Be("MDOT SHA: I-95 at MD 43");
        camera.StreamUrl.Should().Be("https://chart.maryland.gov/video/video.php?feed=101");
        camera.SourceName.Should().Be("MDOT SHA");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoUrl()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain("mdot-2");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(MarylandBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static MarylandDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://mdgeodata.md.gov/")
        };
        return new MarylandDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<MarylandDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
