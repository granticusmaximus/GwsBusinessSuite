using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class TexasDotCameraProviderTests
{
    private static readonly BoundingBox TexasBbox = new(North: 37, South: 25, East: -93, West: -107);

    [Fact]
    public async Task GetCamerasAsync_ShouldKeyOnObjectId_NotCameraId_SinceCameraIdIsNotUnique()
    {
        // Camera_ID is deliberately "1" for both records here - a real, confirmed gotcha where
        // this field is NOT unique across the live dataset; OBJECTID is what's actually unique.
        var page = FeaturePage(
            (21, "1", "IH-20 @ Buck Sherrod Rd", "https://images-webcams.windy.com/98/1709573498/current/full/1709573498.jpg", "Active"),
            (22, "1", "IH-20 @ Information Center", "https://images-webcams.windy.com/92/1666632992/current/full/1666632992.jpg", "Active"));
        var provider = CreateProvider(_ => JsonResponse(page));

        var result = await provider.GetCamerasAsync(TexasBbox);

        result.Should().HaveCount(2);
        result.Select(c => c.Id).Should().BeEquivalentTo(["txdot-21", "txdot-22"], "OBJECTID must be used as the unique key, not the duplicated Camera_ID");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeNonActiveCameras()
    {
        var page = FeaturePage((1, "1", "Inactive", "https://images-webcams.windy.com/1.jpg", "Inactive"));
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
                ? FeaturePage(Enumerable.Range(1, 1000).Select(i => (i, i.ToString(), "Camera", $"https://images-webcams.windy.com/{i}.jpg", "Active")).ToArray())
                : FeaturePage((1001, "1001", "Last camera", "https://images-webcams.windy.com/1001.jpg", "Active"));
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

        var result = await provider.GetCamerasAsync(TexasBbox);

        result.Should().BeEmpty();
    }

    private static string FeaturePage(params (int ObjectId, string CameraId, string EquipmentName, string Url, string Status)[] cameras)
    {
        var features = string.Join(",", cameras.Select(c =>
            $$"""{ "attributes": { "OBJECTID": {{c.ObjectId}}, "Camera_ID": "{{c.CameraId}}", "Equipment_Name": "{{c.EquipmentName}}", "url": "{{c.Url}}", "status": "{{c.Status}}" }, "geometry": { "x": -97.7, "y": 30.3 } }"""));
        return $$"""{ "features": [{{features}}] }""";
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static TexasDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services7.arcgis.com/bF49JeI2xZRhCsD9/")
        };
        return new TexasDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<TexasDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
