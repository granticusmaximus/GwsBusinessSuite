using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NewYorkThruwayIncidentProviderTests
{
    // Shaped from a live fetch of webdataapi.thruway.ny.gov/api/data/events/xml - every <event>
    // is a self-closing element carrying its data as attributes, confirmed live.
    private const string SampleXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <events>
          <lastupdatetime>9/27/2026 8:29:34 PM</lastupdatetime>
          <event category="incident" eventid="CAD-262700257" updatetime="9/27/2026 7:41 PM" orgid="NYS Thruway Authority" eventtype="crash" latitude="42.38679" longitude="-79.48299" milepost="478.8" route="I-90 - NYS Thruway" region="BU" direction="eastbound" eventdesc="crash I-90 eastbound between exit 60 and exit 59" expirationdatetime="Until further notice"></event>
        </events>
        """;

    private static readonly BoundingBox NyBbox = new(North: 45, South: 40, East: -73, West: -80);

    [Fact]
    public async Task GetIncidentsAsync_ShouldParseAttributesFromSelfClosingEventElements()
    {
        var provider = CreateProvider(_ => XmlResponse(SampleXml));

        var result = await provider.GetIncidentsAsync(NyBbox);

        result.Should().ContainSingle();
        var incident = result[0];
        incident.Id.Should().Be("ny-thruway-CAD-262700257");
        incident.RoadwayName.Should().Be("I-90 - NYS Thruway");
        incident.Description.Should().Be("crash I-90 eastbound between exit 60 and exit 59");
        incident.EventType.Should().Be("crash");
        incident.Severity.Should().Be("incident");
        incident.Latitude.Should().Be(42.38679);
        incident.Longitude.Should().Be(-79.48299);
        incident.SourceName.Should().Be("NY Thruway");
    }

    [Fact]
    public async Task GetIncidentsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetIncidentsAsync(NyBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage XmlResponse(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml")
    };

    private static NewYorkThruwayIncidentProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://webdataapi.thruway.ny.gov/")
        };
        return new NewYorkThruwayIncidentProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<NewYorkThruwayIncidentProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
