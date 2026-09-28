using System.Globalization;
using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class DriveBcCameraProviderTests
{
    // Shaped from a live download of DriveBC's real webcams.csv - includes a quoted field with an
    // embedded comma (highway_locationDescription), confirmed present in the real file, which is
    // exactly what this provider's own minimal CSV parser has to handle correctly.
    private const string SampleCsv =
        "links_bchighwaycam,links_imageDisplay,links_imageThumbnail,links_replayTheDay,id,highway_number,highway_locationDescription,camName,caption,credit,orientation,latitude,longitude\n" +
        "https://images.drivebc.ca/bchighwaycam/pub/html/www/2.html,https://images.drivebc.ca/bchighwaycam/pub/cameras/2.jpg,https://images.drivebc.ca/bchighwaycam/pub/cameras/tn/2.jpg,https://images.drivebc.ca/ReplayTheDay/player.html?cam=2,2,5,\"Coquihalla, Great Bear Snowshed\",Coquihalla Great Bear Snowshed - N,\"Highway 5 at the Great Bear Snowshed, looking north.\",,N,49.596374 ,-121.159832 \n" +
        "https://images.drivebc.ca/bchighwaycam/pub/html/www/9.html,,https://images.drivebc.ca/bchighwaycam/pub/cameras/tn/9.jpg,https://images.drivebc.ca/ReplayTheDay/player.html?cam=9,9,1,,No image,No image camera,,N,49.0,-122.0\n";

    private static readonly BoundingBox BcBbox = new(North: 60, South: 48, East: -114, West: -139);

    [Fact]
    public async Task GetCamerasAsync_ShouldParseQuotedCsvFieldsWithEmbeddedCommas()
    {
        var provider = CreateProvider(_ => CsvResponse(SampleCsv));

        var result = await provider.GetCamerasAsync(BcBbox);

        result.Should().ContainSingle("the second row has no image and should be excluded");
        var camera = result[0];
        camera.Id.Should().Be("drivebc-2");
        camera.Name.Should().Be("DriveBC: Coquihalla Great Bear Snowshed - N");
        camera.StreamUrl.Should().Be("https://images.drivebc.ca/bchighwaycam/pub/cameras/2.jpg");
        camera.Latitude.Should().Be(49.596374, "trailing spaces in the CSV lat/lon values must be trimmed");
        camera.Longitude.Should().Be(-121.159832);
        camera.SourceName.Should().Be("DriveBC");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldParseCoordinatesUnderACommaDecimalCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var provider = CreateProvider(_ => CsvResponse(SampleCsv));

            var result = await provider.GetCamerasAsync(BcBbox);

            result.Should().ContainSingle();
            result[0].Latitude.Should().Be(49.596374);
            result[0].Longitude.Should().Be(-121.159832);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(BcBbox);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage CsvResponse(string csv) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(csv, System.Text.Encoding.UTF8, "text/csv")
    };

    private static DriveBcCameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var http = new HttpClient(new RecordingHandler(responseFactory))
        {
            BaseAddress = new Uri("https://catalogue.data.gov.bc.ca/")
        };
        return new DriveBcCameraProvider(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<DriveBcCameraProvider>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
