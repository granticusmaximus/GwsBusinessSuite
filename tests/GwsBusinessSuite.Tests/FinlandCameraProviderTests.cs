using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class FinlandCameraProviderTests
{
    // Shaped from a live query against tie.digitraffic.fi/api/weathercam/v1/stations - each
    // station Feature carries multiple camera "presets" (different physical angles at the same
    // site), confirmed live, flattened here into individual CameraFeeds sharing the station's
    // coordinates.
    private const string SampleJson = """
        {
          "features": [
            {
              "id": "C01503",
              "geometry": { "coordinates": [23.99616, 60.05374, 0.0] },
              "properties": {
                "name": "kt51_Inkoo",
                "presets": [
                  { "id": "C0150301", "inCollection": true },
                  { "id": "C0150302", "inCollection": true },
                  { "id": "C0150309", "inCollection": false }
                ]
              }
            }
          ]
        }
        """;

    private static readonly BoundingBox FinlandBbox = new(North: 70, South: 59, East: 32, West: 20);

    [Fact]
    public async Task GetCamerasAsync_ShouldFlattenPresetsIntoIndividualCameras()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(FinlandBbox);

        result.Should().HaveCount(2, "only the 2 in-collection presets should be surfaced");
        result.Should().OnlyContain(c => c.Latitude == 60.05374 && c.Longitude == 23.99616, "all presets at one station share its coordinates");
        result.Select(c => c.Id).Should().BeEquivalentTo(["fintraffic-C0150301", "fintraffic-C0150302"]);
        result.Should().OnlyContain(c => c.Name == "Fintraffic: kt51_Inkoo");
        result[0].StreamUrl.Should().Be("https://weathercam.digitraffic.fi/C0150301.jpg");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(FinlandBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static FinlandCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://tie.digitraffic.fi/")
        };
        return new FinlandCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<FinlandCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
