using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Geocoding;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class GeocodingServiceTests
{
    private const string PhotonHit = """
        {"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-84.3880,33.7490]},"properties":{"name":"Atlanta"}}]}
        """;
    private const string PhotonMiss = """{"type":"FeatureCollection","features":[]}""";

    private const string CensusHit = """
        {"result":{"addressMatches":[{"coordinates":{"x":-77.0365525,"y":38.8976387}}]}}
        """;
    private const string CensusMiss = """{"result":{"addressMatches":[]}}""";

    [Fact]
    public async Task GeocodeAsync_ShouldReturnPhotonsResult_WhenPhotonFindsAMatch()
    {
        var service = CreateService(
            photon: _ => JsonResponse(PhotonHit),
            census: _ => throw new InvalidOperationException("Census should not be called when Photon already found a match."));

        var result = await service.GeocodeAsync("Atlanta, GA");

        result.Should().Be(new GeocodeResult(33.7490, -84.3880));
    }

    [Fact]
    public async Task GeocodeAsync_ShouldFallBackToCensus_WhenPhotonFindsNothing()
    {
        var service = CreateService(photon: _ => JsonResponse(PhotonMiss), census: _ => JsonResponse(CensusHit));

        var result = await service.GeocodeAsync("1600 Pennsylvania Ave NW, Washington, DC 20500");

        result.Should().Be(new GeocodeResult(38.8976387, -77.0365525));
    }

    [Fact]
    public async Task GeocodeAsync_ShouldReturnNull_WhenNeitherProviderFindsAMatch()
    {
        var service = CreateService(photon: _ => JsonResponse(PhotonMiss), census: _ => JsonResponse(CensusMiss));

        var result = await service.GeocodeAsync("a place that does not exist anywhere");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GeocodeAsync_ShouldFallBackToCensus_WhenPhotonRequestFails()
    {
        var service = CreateService(photon: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError), census: _ => JsonResponse(CensusHit));

        var result = await service.GeocodeAsync("1600 Pennsylvania Ave NW, Washington, DC 20500");

        result.Should().Be(new GeocodeResult(38.8976387, -77.0365525));
    }

    [Fact]
    public async Task GeocodeAsync_ShouldReturnNull_RatherThanThrow_WhenBothProvidersFail()
    {
        var service = CreateService(
            photon: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            census: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await service.GeocodeAsync("Atlanta, GA");

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GeocodeAsync_ShouldReturnNull_ForBlankQueries_WithoutCallingEitherProvider(string query)
    {
        var service = CreateService(
            photon: _ => throw new InvalidOperationException("Photon should not be called for a blank query."),
            census: _ => throw new InvalidOperationException("Census should not be called for a blank query."));

        var result = await service.GeocodeAsync(query);

        result.Should().BeNull();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static GeocodingService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> photon,
        Func<HttpRequestMessage, HttpResponseMessage> census)
    {
        var photonClient = new HttpClient(new RecordingHandler(photon)) { BaseAddress = new Uri("https://photon.komoot.io/") };
        var censusClient = new HttpClient(new RecordingHandler(census)) { BaseAddress = new Uri("https://geocoding.geo.census.gov/") };
        return new GeocodingService(
            new PhotonGeocoder(photonClient, NullLogger<PhotonGeocoder>.Instance),
            new CensusGeocoder(censusClient, NullLogger<CensusGeocoder>.Instance));
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
