using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class OregonDotCameraProviderTests
{
    private static readonly BoundingBox OregonBbox = new(North: 46, South: 42, East: -116, West: -125);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasRequestingWgs84OutputSpatialReference()
    {
        var page = FeaturePage(("1", "I-5 at Portland", "https://tripcheck.com/cameras/1.jpg"));
        var provider = CreateProvider(request =>
        {
            request.RequestUri!.ToString().Should().Contain("outSR=4326", "this layer's native geometry is Web Mercator, so outSR=4326 must always be requested");
            return JsonResponse(page);
        });

        var result = await provider.GetCamerasAsync(OregonBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("odot-1");
        camera.Name.Should().Be("ODOT: I-5 at Portland");
        camera.SourceName.Should().Be("ODOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoFilename()
    {
        var page = FeaturePage(("2", "No image", ""));
        var provider = CreateProvider(_ => JsonResponse(page));

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldPageThroughResultsUntilAShortPageIsReturned()
    {
        var requestCount = 0;
        var provider = CreateProvider(request =>
        {
            requestCount++;
            var url = request.RequestUri!.ToString();
            var offset = url.Contains("resultOffset=1000") ? 1000 : 0;
            var page = offset == 0
                ? FeaturePage(Enumerable.Range(1, 1000).Select(i => (i.ToString(), "Camera", $"https://tripcheck.com/cameras/{i}.jpg")).ToArray())
                : FeaturePage(("1001", "Last camera", "https://tripcheck.com/cameras/1001.jpg"));
            return JsonResponse(page);
        });

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        requestCount.Should().Be(2, "a full 1000-record page should trigger one more paged request");
        result.Should().HaveCount(1001);
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(OregonBbox);

        result.Should().BeEmpty();
    }

    private static string FeaturePage(params (string ObjectId, string Title, string Filename)[] cameras)
    {
        var features = string.Join(",", cameras.Select(c =>
            $$"""{ "attributes": { "ObjectId": {{c.ObjectId}}, "attributes_title": "{{c.Title}}", "attributes_filename": "{{c.Filename}}" }, "geometry": { "x": -122.6, "y": 45.5 } }"""));
        return $$"""{ "features": [{{features}}] }""";
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static OregonDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services.arcgis.com/uUvqNMGPm7axC2dD/")
        };
        return new OregonDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<OregonDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
