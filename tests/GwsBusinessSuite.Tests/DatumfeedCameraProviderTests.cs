using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class DatumfeedCameraProviderTests
{
    // Shaped from a live call to GET /api/cameras during implementation - real field names and
    // nesting, including a wsdot-cameras entry (deliberately skipped - see the provider's own
    // comment) and a commercialOk:false entry (also skipped, even though none of the 7 real
    // registries were actually false as of implementation).
    private const string SampleJson = """
        {
          "cameras": [
            {
              "id": "cal-1-dn1015thllookingn", "name": "DN-101: 5th & L - Looking North",
              "lat": 41.755345, "lon": -124.195567,
              "feedUrl": "https://cwwp2.dot.ca.gov/data/d1/cctv/image/dn1015thllookingn/dn1015thllookingn.jpg",
              "feedType": "jpeg_poll", "active": true,
              "registry": { "slug": "caltrans-cctv", "name": "Caltrans (California DOT) CCTV Cameras",
                "attribution": "California Department of Transportation (Caltrans)",
                "licenseUrl": "https://dot.ca.gov/conditions-of-use", "commercialOk": null }
            },
            {
              "id": "wsdot-9999", "name": "Duplicate of our own WSDOT provider",
              "lat": 47.0, "lon": -122.0, "feedUrl": "https://example.test/wsdot-9999.jpg",
              "feedType": "jpeg_poll", "active": true,
              "registry": { "slug": "wsdot-cameras", "name": "Washington State DOT (WSDOT) Traffic Cameras",
                "attribution": "WSDOT", "licenseUrl": null, "commercialOk": null }
            },
            {
              "id": "inactive-1", "name": "Inactive camera",
              "lat": 40.0, "lon": -120.0, "feedUrl": "https://example.test/inactive.jpg",
              "feedType": "jpeg_poll", "active": false,
              "registry": { "slug": "caltrans-cctv", "name": "Caltrans", "attribution": "Caltrans", "commercialOk": null }
            },
            {
              "id": "no-commercial-1", "name": "Explicitly non-commercial",
              "lat": 40.0, "lon": -120.0, "feedUrl": "https://example.test/noncommercial.jpg",
              "feedType": "jpeg_poll", "active": true,
              "registry": { "slug": "some-registry", "name": "Some Registry", "attribution": "Some Registry", "commercialOk": false }
            }
          ]
        }
        """;

    private static readonly BoundingBox AnyBbox = new(90, -90, 180, -180);

    [Fact]
    public async Task GetCamerasAsync_ShouldWork_WithNoApiKeyConfigured()
    {
        // Unlike WSDOT/Windy, Datumfeed's anonymous tier already works with no key at all.
        var provider = CreateProvider(apiKey: "", _ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(AnyBbox);

        result.Should().ContainSingle(c => c.Id == "datumfeed-cal-1-dn1015thllookingn");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldMapACameraWithItsRegistryAttribution()
    {
        var provider = CreateProvider("", _ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(AnyBbox);

        var camera = result.Single(c => c.Id == "datumfeed-cal-1-dn1015thllookingn");
        camera.Name.Should().Be("DN-101: 5th & L - Looking North");
        camera.Latitude.Should().Be(41.755345);
        camera.StreamUrl.Should().Be("https://cwwp2.dot.ca.gov/data/d1/cctv/image/dn1015thllookingn/dn1015thllookingn.jpg");
        camera.StreamKind.Should().Be(CameraStreamKind.Snapshot);
        camera.SourceName.Should().Be("California Department of Transportation (Caltrans)", "the specific originating agency should be credited, not just \"Datumfeed\"");
        camera.SourceAttributionUrl.Should().Be("https://dot.ca.gov/conditions-of-use");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeTheWsdotRegistry_ToAvoidDuplicatingOurOwnWsdotProvider()
    {
        var provider = CreateProvider("", _ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(AnyBbox);

        result.Select(c => c.Id).Should().NotContain("datumfeed-wsdot-9999");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeInactiveCameras()
    {
        var provider = CreateProvider("", _ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(AnyBbox);

        result.Select(c => c.Id).Should().NotContain("datumfeed-inactive-1");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldExcludeCamerasWhoseRegistryIsExplicitlyNotCommercialOk()
    {
        var provider = CreateProvider("", _ => JsonResponse(SampleJson));

        var result = await provider.GetCamerasAsync(AnyBbox);

        result.Select(c => c.Id).Should().NotContain("datumfeed-no-commercial-1");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldSendTheApiKeyHeader_WhenConfigured()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider("TEST_KEY", request =>
        {
            capturedRequest = request;
            return JsonResponse(SampleJson);
        });

        await provider.GetCamerasAsync(AnyBbox);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Authorization.Should().NotBeNull();
        capturedRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        capturedRequest.Headers.Authorization.Parameter.Should().Be("TEST_KEY");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider("", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(AnyBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static DatumfeedCameraProvider CreateProvider(
        string apiKey,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory)) { BaseAddress = new Uri("https://datumfeed.com/") };
        return new DatumfeedCameraProvider(
            http,
            Options.Create(new CameraIntelOptions { DatumfeedApiKey = apiKey }),
            NullLogger<DatumfeedCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
