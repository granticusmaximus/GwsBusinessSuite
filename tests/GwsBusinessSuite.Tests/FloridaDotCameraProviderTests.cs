using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class FloridaDotCameraProviderTests
{
    private static readonly BoundingBox FloridaBbox = new(North: 31, South: 24, East: -79, West: -88);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapCamerasAndBuildNameFromHighwayAndDescription()
    {
        var page0 = FeaturePage(
            ("FL1", "Downtown Tampa", "I-275", "https://fl511.com/cameras/FL1.jpg", -82.45, 27.95),
            ("FL2", "No image camera", "I-4", "", -81.3, 28.5));

        var provider = CreateProvider(_ => JsonResponse(page0));

        var result = await provider.GetCamerasAsync(FloridaBbox);

        result.Should().ContainSingle();
        var camera = result[0];
        camera.Id.Should().Be("fl511-FL1");
        camera.Name.Should().Be("FL511: I-275 - Downtown Tampa");
        camera.Latitude.Should().Be(27.95);
        camera.Longitude.Should().Be(-82.45);
        camera.SourceName.Should().Be("FL511");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldPageThroughResultsUntilAShortPageIsReturned()
    {
        var requestCount = 0;
        var provider = CreateProvider(request =>
        {
            requestCount++;
            var url = request.RequestUri!.ToString();
            var offset = url.Contains("resultOffset=2000") ? 2000 : 0;
            var page = offset == 0
                ? FeaturePage(Enumerable.Range(1, 2000).Select(i => ($"FL{i}", "Camera", "I-95", $"https://fl511.com/cameras/FL{i}.jpg", -80.0, 27.0)).ToArray())
                : FeaturePage(("FL2001", "Last camera", "I-95", "https://fl511.com/cameras/FL2001.jpg", -80.0, 27.0));
            return JsonResponse(page);
        });

        var result = await provider.GetCamerasAsync(new BoundingBox(90, -90, 180, -180));

        requestCount.Should().Be(2, "a full 2000-record page should trigger one more paged request");
        result.Should().HaveCount(2001);
        result.Select(c => c.Id).Should().Contain("fl511-FL2001");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(FloridaBbox);

        result.Should().BeEmpty();
    }

    private static string FeaturePage(params (string Id, string Descript, string Highway, string Image, double X, double Y)[] cameras)
    {
        var features = string.Join(",", cameras.Select(c =>
            $$"""{ "attributes": { "ID": "{{c.Id}}", "DESCRIPT": "{{c.Descript}}", "HIGHWAY": "{{c.Highway}}", "IMAGE": "{{c.Image}}" }, "geometry": { "x": {{c.X}}, "y": {{c.Y}} } }"""));
        return $$"""{ "features": [{{features}}] }""";
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static FloridaDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services.arcgis.com/3wFbqsFPLeKqOlIK/")
        };
        return new FloridaDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<FloridaDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
