using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class StreetLevelImageryServiceTests
{
    private const double Lat = 33.7490;
    private const double Lon = -84.3880;

    private const string MapillaryJson = """
        {"data":[
          {"id":"111","thumb_1024_url":"https://scontent.example/far.jpg","captured_at":1714000000000,"compass_angle":92.5,
           "geometry":{"type":"Point","coordinates":[-84.3870,33.7490]}},
          {"id":"222","thumb_1024_url":"https://scontent.example/near.jpg","captured_at":1715000000000,"compass_angle":0,
           "geometry":{"type":"Point","coordinates":[-84.3880,33.7491]}},
          {"id":"333","thumb_1024_url":"https://scontent.example/too-far.jpg","geometry":{"type":"Point","coordinates":[-84.40,33.76]}},
          {"id":"444","geometry":{"type":"Point","coordinates":[-84.3880,33.7490]}}
        ]}
        """;

    [Fact]
    public void ParseMapillary_ShouldKeepPhotosWithinRange_WithDateDirectionAndLink()
    {
        var images = StreetLevelImageryService.ParseMapillary(MapillaryJson, Lat, Lon, 250);

        images.Select(image => image.ImageUrl).Should().BeEquivalentTo("https://scontent.example/far.jpg", "https://scontent.example/near.jpg");
        var near = images.Single(image => image.ImageUrl.EndsWith("near.jpg"));
        near.DistanceMeters.Should().BeApproximately(11.1, 0.5);
        near.CapturedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1715000000000));
        near.ViewUrl.Should().Be("https://www.mapillary.com/app/?pKey=222");
        StreetLevelImageryService.Facing(images.Single(image => image.ImageUrl.EndsWith("far.jpg")).CompassAngle).Should().Be("east");
    }

    [Fact]
    public void ParseKartaView_ShouldReadNumbersSentAsStrings_AndPreferTheLargerImage()
    {
        const string json = """
            {"result":{"data":[
              {"id":"9","sequenceId":"55","sequenceIndex":"3","lat":"33.7492","lng":"-84.3880","heading":"180",
               "shotDate":"2023-06-01 14:30:00","fileurlProc":"https://storage.example/proc.jpg","fileurlTh":"https://storage.example/th.jpg"},
              {"id":"10","lat":33.7491,"lng":-84.3881,"fileurlTh":"//storage.example/only-thumb.jpg"},
              {"id":"11","lat":33.7491,"lng":-84.3881,"fileurl":"http://insecure.example/x.jpg"}
            ]}}
            """;

        var images = StreetLevelImageryService.ParseKartaView(json, Lat, Lon, 250);

        images.Should().HaveCount(2);
        images[0].ImageUrl.Should().Be("https://storage.example/proc.jpg");
        images[0].ViewUrl.Should().Be("https://kartaview.org/details/55/3");
        images[0].CapturedAt.Should().Be(new DateTimeOffset(2023, 6, 1, 14, 30, 0, TimeSpan.Zero));
        StreetLevelImageryService.Facing(images[0].CompassAngle).Should().Be("south");
        images[1].ImageUrl.Should().Be("https://storage.example/only-thumb.jpg");
    }

    [Fact]
    public async Task FindNearAsync_ShouldUseMapillaryFirst_NearestFirst_AndAlwaysOfferGoogle()
    {
        var requested = new List<string>();
        var service = Create("token", request =>
        {
            requested.Add(request.RequestUri!.Host);
            return Json(request.RequestUri.Host == "graph.mapillary.com" ? MapillaryJson : """{"result":{"data":[]}}""");
        });

        var result = await service.FindNearAsync(Lat, Lon);

        result.Images.Select(image => image.ImageUrl).Should().Equal("https://scontent.example/near.jpg");
        requested.Should().Equal("graph.mapillary.com");
        result.GoogleStreetViewUrl.Should().Be("https://www.google.com/maps/@?api=1&map_action=pano&viewpoint=33.749,-84.388");
    }

    [Fact]
    public async Task FindNearAsync_WithoutAToken_ShouldTryKartaViewAndWidenTheSearch()
    {
        var radii = new List<string>();
        var service = Create("", request =>
        {
            request.RequestUri!.Host.Should().Be("api.openstreetcam.org");
            radii.Add(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["radius"]!);
            return Json("""{"result":{"data":[]}}""");
        });

        var result = await service.FindNearAsync(Lat, Lon);

        radii.Should().Equal("60", "250");
        result.Images.Should().BeEmpty();
        result.GoogleStreetViewUrl.Should().Contain("viewpoint=33.749,-84.388");
    }

    [Fact]
    public async Task FindNearAsync_ShouldNotFailWhenASourceErrors()
    {
        var service = Create("token", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await service.FindNearAsync(Lat, Lon);

        result.Images.Should().BeEmpty();
        result.GoogleStreetViewUrl.Should().NotBeNullOrEmpty();
    }

    private static StreetLevelImageryService Create(string token, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(respond)), Options.Create(new CameraIntelOptions { MapillaryAccessToken = token }),
            NullLogger<StreetLevelImageryService>.Instance);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
