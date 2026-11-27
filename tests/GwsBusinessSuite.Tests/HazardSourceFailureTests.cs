using System.Net;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// 27-11-26, found by the Overwatch visual tour: GDACS hung until the 25-second timeout, and because a
// failed source wasn't remembered, every pan and refresh waited for it again and the Hazards layer drew
// nothing. A failed source is now left alone for HazardLayerService.SourceRetryAfter.
public sealed class HazardSourceFailureTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int GdacsRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "www.gdacs.org")
            {
                Interlocked.Increment(ref GdacsRequests);
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 25 seconds elapsing.");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task AFailedSource_ShouldNotBeAskedAgain_OnTheNextPan()
    {
        var handler = new Handler();
        var service = new HazardLayerService(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<HazardLayerService>.Instance);
        var world = new BoundingBox(80, -80, 180, -180);

        await service.GetHazardsAsync(world);
        var afterFirst = handler.GdacsRequests;
        await service.GetHazardsAsync(world);
        await service.GetHazardsAsync(new BoundingBox(60, 0, 40, -20));

        Assert.Equal(2, afterFirst);
        Assert.Equal(afterFirst, handler.GdacsRequests);
        Assert.Equal(TimeSpan.FromMinutes(5), HazardLayerService.SourceRetryAfter);
    }
}
