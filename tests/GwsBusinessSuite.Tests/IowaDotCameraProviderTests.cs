using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class IowaDotCameraProviderTests
{
    // Shaped from a live query against Iowa DOT's ArcGIS Hub GeoJSON download during
    // implementation - coordinates are GeoJSON's own [longitude, latitude] order.
    private const string SampleJson = """
        {
          "features": [
            { "properties": { "device_id": 701, "Desc_": "I-80 at Mile 123", "ImageName": "I-80 EB", "ImageURL": "https://www.iowadot.gov/cameras/701.jpg", "Type": "Traffic" }, "geometry": { "coordinates": [-93.6, 41.6] } },
            { "properties": { "device_id": 702, "Desc_": "No image camera", "ImageName": null, "ImageURL": "", "Type": "RWIS" }, "geometry": { "coordinates": [-93.7, 41.7] } },
            { "properties": { "device_id": 703, "Desc_": "Missing geometry", "ImageName": "Bad", "ImageURL": "https://www.iowadot.gov/cameras/703.jpg", "Type": "Traffic" }, "geometry": null }
          ]
        }
        """;

    private static readonly BoundingBox IowaBbox = new(North: 44, South: 40, East: -90, West: -97);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasUsingGeoJsonLonLatOrder()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(IowaBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("iowadot-701");
        camera.Name.Should().Be("Iowa DOT: I-80 EB", "ImageName should be preferred over Desc_ when both are present");
        camera.Latitude.Should().Be(41.6);
        camera.Longitude.Should().Be(-93.6);
        camera.SourceName.Should().Be("Iowa DOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoImageOrNoGeometry()
    {
        var provider = CreateProvider(_ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Select(c => c.Id).Should().NotContain(["iowadot-702", "iowadot-703"]);
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(IowaBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static IowaDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://hub.arcgis.com/")
        };
        return new IowaDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<IowaDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
