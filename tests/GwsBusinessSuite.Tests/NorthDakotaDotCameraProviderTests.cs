using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NorthDakotaDotCameraProviderTests
{
    // Shaped from a live query against travelfiles.dot.nd.gov/geojson_nc/cameras.json - one
    // Feature is a physical site whose "Cameras" array holds multiple individual camera views.
    private const string SampleJson = """
        {
          "features": [
            {
              "id": "1",
              "geometry": { "type": "Point", "coordinates": [-103.21688, 48.34177] },
              "properties": {
                "Region": "Williston Area",
                "Cameras": [
                  { "Description": "Ray - West (US 2 MP 51.3) - NDDOT", "FullPath": "https://www.dot.nd.gov/travel-info/cameras/US2RP51Raywest.jpg" },
                  { "Description": "Ray - North (US 2 MP 51.3) - NDDOT", "FullPath": "https://www.dot.nd.gov/travel-info/cameras/US2RP51Raycenter.jpg" },
                  { "Description": "No path", "FullPath": "" }
                ]
              }
            }
          ]
        }
        """;

    private static readonly BoundingBox NorthDakotaBbox = new(North: 49, South: 45, East: -96, West: -104.5);

    [Fact]
    public async Task GetCamerasAsync_ShouldFlattenEachClustersCamerasIntoIndividualFeeds()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(NorthDakotaBbox);

        result.Should().HaveCount(2, "the cluster has 2 cameras with a real FullPath and one without");
        result.Should().OnlyContain(c => c.Latitude == 48.34177 && c.Longitude == -103.21688, "all cameras in one cluster share the site's coordinates");
        result.Select(c => c.Name).Should().Contain("NDDOT: Ray - West (US 2 MP 51.3) - NDDOT");
        result.Should().OnlyContain(c => c.SourceName == "NDDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(NorthDakotaBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static NorthDakotaDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://travelfiles.dot.nd.gov/")
        };
        return new NorthDakotaDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<NorthDakotaDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
