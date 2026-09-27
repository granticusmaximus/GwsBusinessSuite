using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class CaltransCameraProviderTests
{
    // Shaped from a live query against cwwp2.dot.ca.gov during implementation - lat/lon are
    // served as strings, not numbers, which is why TryParseCoordinate exists.
    private const string District3Json = """
        {
          "data": [
            { "cctv": { "index": "d3-1", "location": { "locationName": "I-80 at Sacramento", "latitude": "38.5816", "longitude": "-121.4944" }, "inService": "true", "imageData": { "static": { "currentImageURL": "https://cwwp2.dot.ca.gov/data/d3/cctv/d3-1.jpg" } } } },
            { "cctv": { "index": "d3-2", "location": { "locationName": "Out of service camera", "latitude": "38.6", "longitude": "-121.5" }, "inService": "false", "imageData": { "static": { "currentImageURL": "https://cwwp2.dot.ca.gov/data/d3/cctv/d3-2.jpg" } } } },
            { "cctv": { "index": "d3-3", "location": { "locationName": "Null island", "latitude": "0", "longitude": "0" }, "inService": "true", "imageData": { "static": { "currentImageURL": "https://cwwp2.dot.ca.gov/data/d3/cctv/d3-3.jpg" } } } }
          ]
        }
        """;

    private const string EmptyDistrictJson = """{ "data": [] }""";

    private static readonly BoundingBox CaliforniaBbox = new(North: 42, South: 32, East: -114, West: -125);

    [Fact]
    public async Task GetCamerasAsync_ShouldParseStringLatLonAndMapOnlyInServiceCameras()
    {
        var provider = CreateProvider(request =>
            request.RequestUri!.ToString().Contains("cctvStatusD03.json") ? JsonResponse(District3Json) : JsonResponse(EmptyDistrictJson));

        var result = await provider.GetCamerasAsync(CaliforniaBbox);

        result.Should().ContainSingle("only the in-service camera with real coordinates should survive");
        var camera = result[0];
        camera.Id.Should().Be("caltrans-d3-d3-1");
        camera.Name.Should().Be("Caltrans: I-80 at Sacramento");
        camera.Latitude.Should().Be(38.5816);
        camera.Longitude.Should().Be(-121.4944);
        camera.SourceName.Should().Be("Caltrans");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeOutOfServiceAndNullIslandCameras()
    {
        var provider = CreateProvider(request =>
            request.RequestUri!.ToString().Contains("cctvStatusD03.json") ? JsonResponse(District3Json) : JsonResponse(EmptyDistrictJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain(["caltrans-d3-d3-2", "caltrans-d3-d3-3"]);
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldQueryAllTwelveDistrictsInParallel()
    {
        var requestedDistricts = new System.Collections.Concurrent.ConcurrentBag<string>();
        var provider = CreateProvider(request =>
        {
            requestedDistricts.Add(request.RequestUri!.ToString());
            return JsonResponse(EmptyDistrictJson);
        });

        await provider.GetCamerasAsync(CaliforniaBbox);

        for (var d = 1; d <= 12; d++)
        {
            requestedDistricts.Should().Contain(url => url.Contains($"cctvStatusD{d:D2}.json"), $"district {d} should be queried");
        }
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmptyForAFailingDistrict_WithoutFailingOtherDistricts()
    {
        var provider = CreateProvider(request =>
            request.RequestUri!.ToString().Contains("cctvStatusD03.json")
                ? JsonResponse(District3Json)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(CaliforniaBbox);

        result.Should().ContainSingle("district 3's camera should still come through even though every other district fails");
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static CaltransCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://cwwp2.dot.ca.gov/")
        };
        return new CaltransCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<CaltransCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
