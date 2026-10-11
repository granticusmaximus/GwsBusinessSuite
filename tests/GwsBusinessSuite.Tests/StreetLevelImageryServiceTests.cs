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
    public async Task FindNearAsync_ShouldUseMapillaryFirst_NearestFirst()
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
    }

    [Fact]
    public async Task FindNearAsync_ShouldNotFailWhenASourceErrors()
    {
        var service = Create("token", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await service.FindNearAsync(Lat, Lon);

        result.Images.Should().BeEmpty();
    }

    // ---- 360° panoramas ----

    private const string PanoramaJson = """
        {"data":[
          {"id":"1001","is_pano":true,"captured_at":1716000000000,"compass_angle":10,"computed_compass_angle":12.5,
           "geometry":{"type":"Point","coordinates":[-84.3900,33.7500]},"computed_geometry":{"type":"Point","coordinates":[-84.3880,33.7490]}},
          {"id":"1002","is_pano":true,"compass_angle":90,"geometry":{"type":"Point","coordinates":[-84.3880,33.74918]}},
          {"id":"1003","is_pano":false,"compass_angle":0,"geometry":{"type":"Point","coordinates":[-84.3880,33.7490]}},
          {"id":"bad","is_pano":true,"geometry":{"type":"Point","coordinates":[-84.3880,33.7490]}}
        ]}
        """;

    [Fact]
    public void ParsePanoramas_ShouldPreferComputedPositions_AndSkipFlatOrUnsafeEntries()
    {
        var panoramas = StreetLevelImageryService.ParsePanoramas(PanoramaJson, Lat, Lon, 250);

        panoramas.Select(p => p.Id).Should().Equal("1001", "1002");
        panoramas[0].DistanceMeters.Should().BeLessThan(1, "its computed position is exactly the drop point");
        panoramas[0].CompassAngle.Should().Be(12.5);
        panoramas[0].CapturedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1716000000000));
        panoramas[1].DistanceMeters.Should().BeApproximately(20, 1);
    }

    [Fact]
    public void BuildLinks_ShouldOfferTheNearestPanoramaInEachDirection_WithinWalkingRange()
    {
        var here = new StreetPanorama("0", Lat, Lon, 0, null, 0);
        StreetPanorama At(string id, double north, double east) =>
            new(id, Lat + north / 111_320d, Lon + east / (111_320d * Math.Cos(Lat * Math.PI / 180)), 0, null, 0);

        var links = StreetLevelImageryService.BuildLinks(here,
        [
            here,
            At("north-10", 10, 0), At("north-25", 25, 0),   // same direction: only the nearer one
            At("east-15", 0, 15),
            At("south-1", -1, 0),                            // too close: the same spot
            At("west-80", 0, -80)                            // too far for one step
        ]);

        links.Select(link => link.Id).Should().Equal("north-10", "east-15");
        links[0].Bearing.Should().BeApproximately(0, 0.5);
        links[1].Bearing.Should().BeApproximately(90, 0.5);
        links[1].DistanceMeters.Should().BeApproximately(15, 0.5);
    }

    [Theory]
    [InlineData(33.75, -84.39, 33.76, -84.39, 0)]
    [InlineData(33.75, -84.39, 33.75, -84.38, 90)]
    [InlineData(33.75, -84.39, 33.74, -84.39, 180)]
    [InlineData(33.75, -84.39, 33.75, -84.40, 270)]
    public void Bearing_ShouldBeACompassHeading(double lat1, double lon1, double lat2, double lon2, double expected)
    {
        StreetLevelImageryService.Bearing(lat1, lon1, lat2, lon2).Should().BeApproximately(expected, 0.5);
    }

    [Fact]
    public async Task GetSceneAsync_ShouldStandOnTheNearestPanorama_AndListWhereYouCanWalk()
    {
        var requests = new List<string>();
        var service = Create("token", request =>
        {
            requests.Add(request.RequestUri!.Query);
            return Json(PanoramaJson);
        });

        var scene = await service.GetSceneAsync(Lat, Lon);

        scene!.Current.Id.Should().Be("1001");
        scene.Links.Should().ContainSingle(link => link.Id == "1002").Which.Bearing.Should().BeApproximately(0, 1);
        requests.Should().OnlyContain(query => query.Contains("is_pano=true"));
        (await service.GetSceneAsync(Lat, Lon, "1002"))!.Current.Id.Should().Be("1002");
    }

    [Fact]
    public async Task GetSceneAsync_WithoutAToken_ShouldReturnNothing_WithoutCallingOut()
    {
        var service = Create("", _ => throw new InvalidOperationException("no request expected"));

        (await service.GetSceneAsync(Lat, Lon)).Should().BeNull();
        service.HasPanoramaSource.Should().BeFalse();
    }

    [Fact]
    public async Task GetPanoramaImageAsync_ShouldLookUpThePictureThenDownloadIt_AndRefuseOddIdsOrNonImages()
    {
        var service = Create("token", request => request.RequestUri!.Host switch
        {
            "graph.mapillary.com" => Json("""{"thumb_2048_url":"https://scontent.example/pano.jpg","id":"1001"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/jpeg") } }
            }
        });

        var image = await service.GetPanoramaImageAsync("1001");
        image!.Value.ContentType.Should().Be("image/jpeg");
        image.Value.Content.Should().Equal(1, 2, 3);
        (await service.GetPanoramaImageAsync("../etc")).Should().BeNull();

        var htmlInstead = Create("token", request => request.RequestUri!.Host == "graph.mapillary.com"
            ? Json("""{"thumb_2048_url":"https://scontent.example/pano.jpg"}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", System.Text.Encoding.UTF8, "text/html") });
        (await htmlInstead.GetPanoramaImageAsync("1001")).Should().BeNull();
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
