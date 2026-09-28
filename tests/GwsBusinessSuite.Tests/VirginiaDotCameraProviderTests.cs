using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class VirginiaDotCameraProviderTests
{
    private static readonly BoundingBox VirginiaBbox = new(North: 40, South: 36, East: -75, West: -84);

    [Fact]
    public async Task GetCamerasAsync_ShouldMapActiveCamerasUsingImageUrl()
    {
        var page = FeaturePage(
            ("86188", "I-64 / MM 279.5 / WB / OL AT CHESAPEAKE BLVD", "https://images.skyvdn.com/thumbs/HamptonRoads769.flv.png", "true"),
            ("109679", "Inactive camera", "https://images.skyvdn.com/thumbs/inactive.flv.png", "false"));
        var provider = CreateProvider(_ => JsonResponse(page));

        var result = await provider.GetCamerasAsync(VirginiaBbox);

        result.Should().ContainSingle("only the active camera should survive");
        var camera = result[0];
        camera.Id.Should().Be("vdot-86188");
        camera.Name.Should().Be("VDOT: I-64 / MM 279.5 / WB / OL AT CHESAPEAKE BLVD");
        camera.StreamUrl.Should().Be("https://images.skyvdn.com/thumbs/HamptonRoads769.flv.png");
        camera.SourceName.Should().Be("VDOT");
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
                ? FeaturePage(Enumerable.Range(1, 1000).Select(i => (i.ToString(), "Camera", $"https://images.skyvdn.com/thumbs/{i}.png", "true")).ToArray())
                : FeaturePage(("1001", "Last camera", "https://images.skyvdn.com/thumbs/1001.png", "true"));
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

        var result = await provider.GetCamerasAsync(VirginiaBbox);

        result.Should().BeEmpty();
    }

    private static string FeaturePage(params (string Id, string Descriptio, string ImageUrl, string Active)[] cameras)
    {
        var features = string.Join(",", cameras.Select(c =>
            $$"""{ "attributes": { "FID": {{c.Id}}, "descriptio": "{{c.Descriptio}}", "image_url": "{{c.ImageUrl}}", "active": "{{c.Active}}" }, "geometry": { "x": -77.4, "y": 37.5 } }"""));
        return $$"""{ "features": [{{features}}] }""";
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static VirginiaDotCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://services.arcgis.com/hRUr1F8lE8Jq2uJo/")
        };
        return new VirginiaDotCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<VirginiaDotCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
