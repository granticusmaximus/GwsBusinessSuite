using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Weather;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NwsForecastServiceTests
{
    // Shaped from real api.weather.gov responses captured during implementation (points ->
    // forecast + observationStations -> nearest station's own observations/latest).
    private const string PointsJson = """
        {"properties":{"forecast":"https://api.weather.gov/gridpoints/FFC/51,87/forecast","observationStations":"https://api.weather.gov/gridpoints/FFC/51,87/stations"}}
        """;
    private const string ForecastJson = """
        {"properties":{"periods":[{"temperature":90,"shortForecast":"Chance Showers And Thunderstorms","detailedForecast":"A chance of showers.","windSpeed":"5 mph","windDirection":"E","probabilityOfPrecipitation":{"value":50}}]}}
        """;
    private const string StationsJson = """
        {"features":[{"properties":{"stationIdentifier":"KATL"}}]}
        """;
    private const string ObservationJson = """
        {"properties":{"temperature":{"value":28},"textDescription":"Mostly Clear"}}
        """;

    [Fact]
    public async Task GetSnapshotAsync_ShouldCombineForecastAndCurrentObservations()
    {
        var service = CreateService(RoutingResponder());

        var result = await service.GetSnapshotAsync(33.749, -84.388);

        result.Should().NotBeNull();
        result!.CurrentTemperatureFahrenheit.Should().BeApproximately(82.4, 0.1, "28C converted to Fahrenheit");
        result.CurrentConditions.Should().Be("Mostly Clear");
        result.ForecastTemperatureFahrenheit.Should().Be(90);
        result.ShortForecast.Should().Be("Chance Showers And Thunderstorms");
        result.WindSpeed.Should().Be("5 mph");
        result.WindDirection.Should().Be("E");
        result.ChanceOfPrecipitationPercent.Should().Be(50);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldStillReturnForecast_WhenTheObservationStationLookupFails()
    {
        var service = CreateService(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/points/")) return JsonResponse(PointsJson);
            if (path.Contains("/forecast")) return JsonResponse(ForecastJson);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var result = await service.GetSnapshotAsync(33.749, -84.388);

        result.Should().NotBeNull("a stale/offline observation station is routine and must never sink the whole snapshot");
        result!.CurrentTemperatureFahrenheit.Should().BeNull();
        result.CurrentConditions.Should().BeNull();
        result.ForecastTemperatureFahrenheit.Should().Be(90);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldReturnNull_RatherThanThrow_WhenThePointsLookupFails()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await service.GetSnapshotAsync(33.749, -84.388);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldCache_ForTheSameRoundedCoordinate()
    {
        var requestCount = 0;
        var responder = RoutingResponder();
        var service = CreateService(request =>
        {
            requestCount++;
            return responder(request);
        });

        await service.GetSnapshotAsync(33.749, -84.388);
        await service.GetSnapshotAsync(33.7491, -84.3881); // rounds to the same cache key

        // One full fetch makes 4 calls: points, forecast, the station list, and that station's
        // own latest observation - the second call should hit cache and make zero more.
        requestCount.Should().Be(4, "points+forecast+stations+observation should only be fetched once for effectively the same location");
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> RoutingResponder() => request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.Contains("/points/")) return JsonResponse(PointsJson);
        if (path.Contains("/forecast")) return JsonResponse(ForecastJson);
        if (path.Contains("/stations") && !path.Contains("/observations")) return JsonResponse(StationsJson);
        if (path.Contains("/observations/latest")) return JsonResponse(ObservationJson);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/geo+json")
    };

    private static NwsForecastService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory)) { BaseAddress = new Uri("https://api.weather.gov/") };
        return new NwsForecastService(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<NwsForecastService>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
