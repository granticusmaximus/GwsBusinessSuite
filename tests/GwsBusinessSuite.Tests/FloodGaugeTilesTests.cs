using System.Net;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// Flood gauges stopped drawing in production: every pan was a new NOAA query, failures weren't
// remembered, and NOAA answered 429. Fixed tiles, remembered failures, and a pause on 429.
public sealed class FloodGaugeTilesTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int GaugeRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.water.noaa.gov") Interlocked.Increment(ref GaugeRequests);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    // Georgia, about state size: within the gauge zoom.
    private static readonly BoundingBox Georgia = new(35, 30.5, -81, -85.6);

    [Fact]
    public void GaugeTiles_ShouldBeFixedSixDegreeSquares_SoPansShareThem()
    {
        var a = HazardLayerService.GaugeTiles(Georgia);
        var b = HazardLayerService.GaugeTiles(new BoundingBox(34.6, 30.9, -81.4, -85.2));

        Assert.Equal(a, b);
        Assert.All(a, tile => Assert.Equal(HazardLayerService.GaugeTileDegrees, tile.North - tile.South));
        Assert.Contains(a, tile => tile.South == 30 && tile.West == -90);
    }

    [Fact]
    public async Task Gauges_ShouldNotBeAskedAgain_AfterA429_UntilThePauseEnds()
    {
        var handler = new Handler(request => request.RequestUri!.Host == "api.water.noaa.gov"
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Json("{}"));
        var service = new HazardLayerService(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<HazardLayerService>.Instance);

        await service.GetHazardsAsync(Georgia);
        var afterFirst = handler.GaugeRequests;
        await service.GetHazardsAsync(new BoundingBox(34.6, 30.9, -81.4, -85.2));
        await service.GetHazardsAsync(new BoundingBox(36, 32, -80, -84));

        Assert.InRange(afterFirst, 1, HazardLayerService.GaugeTiles(Georgia).Count);
        Assert.Equal(afterFirst, handler.GaugeRequests);
    }

    [Fact]
    public async Task Gauges_ShouldBeAskedOncePerTile_AcrossPans()
    {
        var handler = new Handler(request => Json(request.RequestUri!.Host == "api.water.noaa.gov" ? """{"gauges":[]}""" : "{}"));
        var service = new HazardLayerService(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<HazardLayerService>.Instance);

        await service.GetHazardsAsync(Georgia);
        await service.GetHazardsAsync(new BoundingBox(34.6, 30.9, -81.4, -85.2));

        Assert.Equal(HazardLayerService.GaugeTiles(Georgia).Count, handler.GaugeRequests);
    }
}
