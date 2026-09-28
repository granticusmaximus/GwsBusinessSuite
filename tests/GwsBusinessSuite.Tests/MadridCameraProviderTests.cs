using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class MadridCameraProviderTests
{
    // Shaped from a live fetch of informo.madrid.es/informo/tmadrid/CCTV.kml - coordinates are
    // KML's own lon,lat[,alt] comma-separated string, confirmed live.
    private const string SampleKml = """
        <kml xmlns="http://earth.google.com/kml/2.2">
          <Document>
            <Placemark>
              <description>&lt;img src=https://informo.madrid.es/cameras/Camara06303.jpg?v=1 /&gt;</description>
              <ExtendedData>
                <Data name="Numero"><Value>06303</Value></Data>
                <Data name="Nombre"><Value>PLAZA DE CASTILLA (NORTE)</Value></Data>
              </ExtendedData>
              <Point><coordinates>-3.68894207537291,40.466063829633,10 </coordinates></Point>
            </Placemark>
            <Placemark>
              <ExtendedData>
                <Data name="Numero"><Value>06304</Value></Data>
              </ExtendedData>
              <Point><coordinates>-3.7,40.5,10 </coordinates></Point>
            </Placemark>
          </Document>
        </kml>
        """;

    private static readonly BoundingBox MadridBbox = new(North: 41, South: 40, East: -3, West: -4);

    [Fact]
    public async Task GetCamerasAsync_ShouldParseKmlCoordinatesInLonLatOrder()
    {
        var provider = CreateProvider(_ => XmlResponse(SampleKml));

        var result = await provider.GetCamerasAsync(MadridBbox);

        var camera = result.Single(c => c.Id == "madrid-06303");
        camera.Name.Should().Be("Madrid: PLAZA DE CASTILLA (NORTE)");
        camera.Latitude.Should().Be(40.466063829633, "KML coordinates are lon,lat - latitude must come from the second value");
        camera.Longitude.Should().Be(-3.68894207537291);
        camera.StreamUrl.Should().Be("https://informo.madrid.es/cameras/Camara06303.jpg");
        camera.SourceName.Should().Be("Madrid Traffic");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldFallBackToAGenericName_WhenNombreIsMissing()
    {
        var provider = CreateProvider(_ => XmlResponse(SampleKml));

        var result = await provider.GetCamerasAsync(MadridBbox);

        var camera = result.Single(c => c.Id == "madrid-06304");
        camera.Name.Should().Be("Madrid Camera 06304");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(MadridBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage XmlResponse(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/vnd.google-earth.kml+xml")
    };

    private static MadridCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://informo.madrid.es/")
        };
        return new MadridCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<MadridCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
