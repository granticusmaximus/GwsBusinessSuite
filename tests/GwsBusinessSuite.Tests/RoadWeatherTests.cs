using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.RoadWeather;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class RoadWeatherTests
{
    [Theory]
    [InlineData(30.0, 38.0, 25.0, "Light Snow", RoadIceRisk.IceLikely)]      // frozen pavement + precipitation
    [InlineData(31.0, 36.0, 32.0, "None", RoadIceRisk.IceLikely)]            // frost: pavement at/below dew point
    [InlineData(30.0, 36.0, 20.0, "No Precipitation", RoadIceRisk.NearFreezing)] // cold but dry
    [InlineData(34.0, 40.0, 30.0, "Rain", RoadIceRisk.NearFreezing)]
    [InlineData(48.0, 46.0, 36.0, "No Precipitation", RoadIceRisk.Clear)]
    public void Classify_ShouldJudgeThePavement(double pavement, double air, double dewpoint, string precip, string expected)
    {
        RoadIceRiskClassifier.Classify(pavement, air, dewpoint, precip).Risk.Should().Be(expected);
    }

    [Fact]
    public void Classify_ShouldFallBackToAirTemperature_AndSaySo()
    {
        var (risk, reason) = RoadIceRiskClassifier.Classify(null, 31, 25, "Freezing Rain");

        risk.Should().Be(RoadIceRisk.IceLikely);
        reason.Should().StartWith("air 31").And.Contain("freezing rain");
    }

    [Fact]
    public void Classify_ShouldUseThePavement_EvenWhenTheAirIsAboveFreezing()
    {
        // A bridge deck: air 37 F, deck 31 F, wet.
        RoadIceRiskClassifier.Classify(31, 37, 30, "Rain").Risk.Should().Be(RoadIceRisk.IceLikely);
    }

    [Fact]
    public async Task IterisProvider_ShouldReadMontanaNumbers_AndSouthDakotaStrings()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stale = DateTimeOffset.UtcNow.AddHours(-12).ToUnixTimeSeconds();
        var json = $$$"""
            {"features":[
              {"id":"302007","geometry":{"coordinates":[-104.37,47.426]},"properties":{"name":"Savage",
                "atmos":[{"air_temperature":{"value":36},"dewpoint_temperature":{"value":30},"precip_type":{"value":"Light Snow"},"precip_intensity":{"value":"Light"},"observation_time":{"value":{{{now}}}}}],
                "surface":[{"surface_temperature":{"value":33},"observation_time":{"value":{{{now}}}}},{"surface_temperature":{"value":31},"observation_time":{"value":{{{now}}}}}]}},
              {"id":"SD1","geometry":{"coordinates":[-100.3,44.4]},"properties":{"name":"Pierre",
                "atmos":[{"air_temperature":{"value":"49"},"dewpoint_temperature":{"value":"35"},"precip_type":{"value":"None"},"precip_intensity":{"value":"None"},"observation_time":{"value":"{{{now}}}"}}],
                "surface":[{"surface_temperature":{"value":null},"observation_time":{"value":"{{{now}}}"}}]}},
              {"id":"OLD","geometry":{"coordinates":[-100.0,44.0]},"properties":{"name":"Offline",
                "atmos":[{"air_temperature":{"value":20},"observation_time":{"value":{{{stale}}}}}],"surface":[]}}
            ]}
            """;
        var provider = new IterisRoadWeatherProvider(
            new HttpClient(new StaticHandler(json)), new MemoryCache(new MemoryCacheOptions()), NullLogger<IterisRoadWeatherProvider>.Instance,
            "mt", "511MT", "https://www.511mt.net", new BoundingBox(90, -90, 180, -180));

        var stations = await provider.GetStationsAsync();

        stations.Select(s => s.Name).Should().Equal("Savage", "Pierre");
        var savage = stations[0];
        savage.PavementF.Should().Be(31, "the coldest lane sensor is the one that ices");
        savage.Precipitation.Should().Be("Light Snow");
        savage.Risk.Should().Be(RoadIceRisk.IceLikely);
        var pierre = stations[1];
        pierre.PavementF.Should().BeNull();
        pierre.AirF.Should().Be(49);
        pierre.Risk.Should().Be(RoadIceRisk.Clear);
        pierre.Reason.Should().StartWith("air 49");
    }

    private sealed class StaticHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
