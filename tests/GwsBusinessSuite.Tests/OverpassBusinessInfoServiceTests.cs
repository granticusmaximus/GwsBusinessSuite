using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class OverpassBusinessInfoServiceTests
{
    private const string OverpassSampleJson = """
        {
          "elements": [
            {
              "type": "node", "id": 1, "lat": 33.7491, "lon": -84.3881,
              "tags": { "name": "State Capitol Coffee", "shop": "cafe", "website": "https://example.test/capitol-coffee" }
            },
            {
              "type": "node", "id": 2, "lat": 33.7495, "lon": -84.3885,
              "tags": { "amenity": "parking" }
            },
            {
              "type": "node", "id": 3, "lat": 33.7600, "lon": -84.4000,
              "tags": { "name": "Far Away Diner", "amenity": "restaurant" }
            }
          ]
        }
        """;

    private const string CommonsSampleJson = """
        {
          "query": {
            "pages": {
              "1": { "imageinfo": [{ "thumburl": "https://thumb.wikimedia.org/photo1.jpg" }] },
              "2": { "imageinfo": [{ "thumburl": "https://thumb.wikimedia.org/photo2.jpg" }] }
            }
          }
        }
        """;

    [Fact]
    public async Task GetBusinessCardAsync_ShouldReturnNull_WhenNoNamedPoiIsNearby()
    {
        var service = CreateService(
            overpassJson: """{"elements":[]}""",
            commonsJson: """{"query":{"pages":{}}}""");

        var result = await service.GetBusinessCardAsync(33.7490, -84.3880);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetBusinessCardAsync_ShouldReturnTheNearestNamedPoi_WithPhotosAttached()
    {
        var service = CreateService(OverpassSampleJson, CommonsSampleJson);

        var result = await service.GetBusinessCardAsync(33.7490, -84.3880);

        result.Should().NotBeNull();
        result!.Name.Should().Be("State Capitol Coffee");
        result.Website.Should().Be("https://example.test/capitol-coffee");
        result.Category.Should().Be("cafe");
        result.PhotoUrls.Should().Contain("https://thumb.wikimedia.org/photo1.jpg");
    }

    [Fact]
    public async Task GetBusinessCardAsync_ShouldSkipUnnamedElements_LikeAPlainParkingNode()
    {
        var service = CreateService(OverpassSampleJson, CommonsSampleJson);

        var result = await service.GetBusinessCardAsync(33.7490, -84.3880);

        result!.Name.Should().NotBe("parking");
    }

    [Fact]
    public async Task GetBusinessCardAsync_ShouldReturnNull_RatherThanThrow_WhenOverpassFails()
    {
        var service = CreateService(
            overpassStatus: HttpStatusCode.InternalServerError,
            overpassJson: "",
            commonsJson: """{"query":{"pages":{}}}""");

        var result = await service.GetBusinessCardAsync(33.7490, -84.3880);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetBusinessCardAsync_ShouldStillReturnThePoi_WhenCommonsFailsButOverpassSucceeds()
    {
        var service = CreateService(
            OverpassSampleJson,
            commonsJson: "",
            commonsStatus: HttpStatusCode.InternalServerError);

        var result = await service.GetBusinessCardAsync(33.7490, -84.3880);

        result.Should().NotBeNull();
        result!.Name.Should().Be("State Capitol Coffee");
        result.PhotoUrls.Should().BeEmpty();
    }

    private static OverpassBusinessInfoService CreateService(
        string overpassJson,
        string commonsJson,
        HttpStatusCode overpassStatus = HttpStatusCode.OK,
        HttpStatusCode commonsStatus = HttpStatusCode.OK)
    {
        var handler = new RoutingHandler(overpassJson, overpassStatus, commonsJson, commonsStatus);
        var factory = new FakeHttpClientFactory(handler);
        return new OverpassBusinessInfoService(factory, new MemoryCache(new MemoryCacheOptions()), NullLogger<OverpassBusinessInfoService>.Instance);
    }

    // Real IHttpClientFactory.CreateClient() returns a fresh, independently-disposable HttpClient
    // wrapper each call, backed by a shared/pooled handler - the service's own `using var client =
    // httpClientFactory.CreateClient();` per call relies on that. A fake that returns the exact
    // same HttpClient instance every time would break on the second call once the first `using`
    // disposes it - this fake instead wraps the same handler in a new, non-disposing-the-handler
    // HttpClient each time, matching real factory semantics.
    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RoutingHandler(string overpassJson, HttpStatusCode overpassStatus, string commonsJson, HttpStatusCode commonsStatus) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isOverpass = request.RequestUri!.Host.Contains("overpass-api.de");
            var (json, status) = isOverpass ? (overpassJson, overpassStatus) : (commonsJson, commonsStatus);
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        }
    }
}
