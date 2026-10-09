using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class IterisAtisCameraProviderTests
{
    private static readonly long Fresh = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
    private static readonly long Stale = DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeSeconds();

    // Shaped from live sd.cdn.iteris-atis.com layers (2026-10-09): a site per feature, with one
    // entry per camera view nested under properties.cameras.
    private static string CamerasLayer => $$"""
        { "type": "FeatureCollection", "features": [
          { "id": "CSDATY", "geometry": { "type": "Point", "coordinates": [-97.06, 44.95] },
            "properties": { "name": "Watertown North", "route": "I-29", "cameras": [
              { "id": "1", "name": "Camera Looking South", "description": "I-29 @ MP 179 looking south", "image": "https://sd.cdn.iteris-atis.com/camera_images/CSDATY/1/latest.jpg", "updateTime": {{Fresh}} },
              { "id": "2", "name": "Dead view", "image": "https://sd.cdn.iteris-atis.com/camera_images/CSDATY/2/latest.jpg", "updateTime": {{Stale}} },
              { "id": "3", "name": "Plain http", "image": "http://sd.cdn.iteris-atis.com/camera_images/CSDATY/3/latest.jpg", "updateTime": {{Fresh}} }
            ] } }
        ] }
        """;

    private static string RwisLayer => $$"""
        { "type": "FeatureCollection", "features": [
          { "id": "302070", "geometry": { "type": "Point", "coordinates": [-100.5, 44.3] },
            "properties": { "name": "Pierre", "description": "Pierre RWIS", "route": null, "cameras": [
              { "id": "302070-00-01", "name": null, "description": null, "image": "https://sd.cdn.iteris-atis.com/rwis_images/302070-00-01.jpg", "updateTime": {{Fresh}} },
              { "id": "dup", "name": "Same image as the camera layer", "image": "https://sd.cdn.iteris-atis.com/camera_images/CSDATY/1/latest.jpg", "updateTime": {{Fresh}} }
            ] } },
          { "id": "no-cameras", "geometry": { "type": "Point", "coordinates": [-99.0, 44.0] },
            "properties": { "name": "Sensors only", "cameras": null } }
        ] }
        """;

    private static readonly BoundingBox Everywhere = new(90, -90, 180, -180);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapFreshHttpsViewsFromBothLayers_WithoutDuplicates()
    {
        var provider = CreateProvider(request => request.RequestUri!.AbsolutePath.EndsWith("icons.rwis.geojson")
            ? JsonResponse(RwisLayer)
            : JsonResponse(CamerasLayer));

        var result = await provider.GetCamerasAsync(Everywhere);

        result.Select(c => c.Id).Should().BeEquivalentTo(["iteris-sd-CSDATY-1", "iteris-sd-302070-302070-00-01"]);
        var camera = result.Single(c => c.Id == "iteris-sd-CSDATY-1");
        camera.Name.Should().Be("SD511: Watertown North - I-29 @ MP 179 looking south");
        camera.Latitude.Should().Be(44.95, "GeoJSON coordinates are [lon, lat]");
        camera.Longitude.Should().Be(-97.06);
        camera.StreamKind.Should().Be(CameraStreamKind.Snapshot);
        result.Single(c => c.Id == "iteris-sd-302070-302070-00-01").Name.Should().Be("SD511: Pierre");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldRequestTheStatesOwnCdnLayers()
    {
        var requested = new List<string>();
        var provider = CreateProvider(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return JsonResponse("""{ "features": [] }""");
        });

        await provider.GetCamerasAsync(Everywhere);

        requested.Should().BeEquivalentTo([
            "https://sd.cdn.iteris-atis.com/geojson/icons/metadata/icons.cameras.geojson",
            "https://sd.cdn.iteris-atis.com/geojson/icons/metadata/icons.rwis.geojson"]);
    }

    [Fact]
    public async Task GetCamerasAsync_WhenOneLayerFails_ShouldStillReturnTheOther()
    {
        var provider = CreateProvider(request => request.RequestUri!.AbsolutePath.EndsWith("icons.rwis.geojson")
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : JsonResponse(CamerasLayer));

        var result = await provider.GetCamerasAsync(Everywhere);

        result.Select(c => c.Id).Should().Equal("iteris-sd-CSDATY-1");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldFilterToTheRequestedBox()
    {
        var provider = CreateProvider(request => request.RequestUri!.AbsolutePath.EndsWith("icons.rwis.geojson")
            ? JsonResponse(RwisLayer)
            : JsonResponse(CamerasLayer));

        var result = await provider.GetCamerasAsync(new BoundingBox(North: 45.5, South: 44.5, East: -96.5, West: -97.5));

        result.Select(c => c.Id).Should().Equal("iteris-sd-CSDATY-1");
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static IterisAtisCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        new(
            new HttpClient(new RecordingHandler(responseFactory)),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<IterisAtisCameraProvider>.Instance,
            stateCode: "sd",
            sourceName: "SD511",
            sourceAttributionUrl: "https://www.sd511.org",
            coverage: new BoundingBox(North: 46.2, South: 42.3, East: -96.2, West: -104.3));

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
