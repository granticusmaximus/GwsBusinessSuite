using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class TransportForNswCameraProviderTests
{
    [Fact]
    public async Task Provider_ShouldReadPublicSnapshotCamerasInTheRequestedView()
    {
        var json = """{"features":[{"properties":{"id":"1","title":"M1 at Sydney","href":"https://webcams.transport.nsw.gov.au/one.jpeg"},"geometry":{"coordinates":[151.2,-33.8]}},{"properties":{"id":"2","title":"Not a camera","href":"not-a-url"},"geometry":{"coordinates":[151.2,-33.8]}}]}""";
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new TransportForNswCameraProvider(new HttpClient(new StubHandler(json))
        {
            BaseAddress = new Uri("https://data.livetraffic.com/")
        }, cache, NullLogger<TransportForNswCameraProvider>.Instance);

        var cameras = await provider.GetCamerasAsync(new BoundingBox(-33, -34, 152, 151));

        cameras.Should().ContainSingle().Which.Should().Match<CameraFeed>(camera =>
            camera.Id == "transport-nsw-1" && camera.StreamKind == CameraStreamKind.Snapshot
            && camera.SourceName == "Transport for NSW");
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
    }
}
