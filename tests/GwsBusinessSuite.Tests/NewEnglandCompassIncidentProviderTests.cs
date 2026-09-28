using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NewEnglandCompassIncidentProviderTests
{
    // Shaped from a live query against the real Compass C2C XML Data Portal for Vermont -
    // lat/lon are published as plain integers scaled by 1,000,000, confirmed live.
    private const string SampleXml = """
        <status xmlns="http://its.gov/c2c_icd"><incidentData><net id="Vermont" name="Vermont">
          <incident id="VT25-000259" netId="Vermont" xmlns="http://its.gov/c2c_icd">
            <desc>Rehabilitation of bridge on VT-31 in Poultney.</desc>
            <startLocation><roadway>VT-31</roadway><direction>North</direction><lat>43513494</lat><lon>-73233181</lon></startLocation>
            <severity>Low</severity>
            <eventType>Other</eventType>
          </incident>
        </net></incidentData></status>
        """;

    private static readonly BoundingBox VermontBbox = new(North: 45, South: 42, East: -71, West: -74);

    [Fact]
    public async Task GetIncidentsAsync_ShouldDivideOutTheOneMillionCoordinateScale()
    {
        var provider = CreateProvider(_ => XmlResponse(SampleXml), network: "Vermont", sourceName: "511VT", sourceAttributionUrl: "https://newengland511.org");

        var result = await provider.GetIncidentsAsync(VermontBbox);

        result.Should().ContainSingle();
        var incident = result[0];
        incident.Id.Should().Be("necompass-Vermont-VT25-000259");
        incident.RoadwayName.Should().Be("VT-31");
        incident.Description.Should().Be("Rehabilitation of bridge on VT-31 in Poultney.");
        incident.EventType.Should().Be("Other");
        incident.Severity.Should().Be("Low");
        incident.Latitude.Should().Be(43.513494);
        incident.Longitude.Should().Be(-73.233181);
        incident.SourceName.Should().Be("511VT");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError), network: "Maine", sourceName: "511ME", sourceAttributionUrl: "https://newengland511.org");

        var result = await provider.GetIncidentsAsync(VermontBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage XmlResponse(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml")
    };

    private static NewEnglandCompassIncidentProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory, string network, string sourceName, string sourceAttributionUrl)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://nec-por.ne-compass.com/")
        };
        return new NewEnglandCompassIncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<NewEnglandCompassIncidentProvider>.Instance,
            network, sourceName, sourceAttributionUrl);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
