using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class IllinoisDotCameraProviderTests
{
    private static readonly BoundingBox IllinoisBbox = new(North: 43, South: 37, East: -87, West: -92);

    [Fact]
    public async Task GetCamerasAsync_ShouldUseSnapShotField_NotTheDeadImgPathField()
    {
        // ImgPath is deliberately omitted here entirely - a real trap found during
        // implementation where that field links to an HTML viewer page, not an image; SnapShot
        // is the field that actually resolves to a live image and is the only one this provider
        // should ever read.
        var page = FeaturePage(("1", "I-90 at O'Hare", "https://www.travelmidwest.com/cameras/1.jpg"));
        var provider = CreateProvider(_ => JsonResponse(page));

        var result = await provider.GetCamerasAsync(IllinoisBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("idot-1");
        camera.Name.Should().Be("IDOT: I-90 at O'Hare");
        camera.StreamUrl.Should().Be("https://www.travelmidwest.com/cameras/1.jpg");
        camera.SourceName.Should().Be("IDOT");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWithNoSnapShotUrl()
    {
        var page = FeaturePage(("2", "No snapshot", ""));
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
                ? FeaturePage(Enumerable.Range(1, 1000).Select(i => (i.ToString(), "Camera", $"https://www.travelmidwest.com/cameras/{i}.jpg")).ToArray())
                : FeaturePage(("1001", "Last camera", "https://www.travelmidwest.com/cameras/1001.jpg"));
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

        var result = await provider.GetCamerasAsync(IllinoisBbox);

        result.Should().BeEmpty();
    }

    private static string FeaturePage(params (string ObjectId, string Location, string SnapShot)[] cameras)
    {
        var features = string.Join(",", cameras.Select(c =>
            $$"""{ "attributes": { "OBJECTID": {{c.ObjectId}}, "CameraLocation": "{{c.Location}}", "SnapShot": "{{c.SnapShot}}" }, "geometry": { "x": -87.9, "y": 42.0 } }"""));
        return $$"""{ "features": [{{features}}] }""";
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static IllinoisDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services2.arcgis.com/aIrBD8yn1TDTEXoz/")
        };
        return new IllinoisDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<IllinoisDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
