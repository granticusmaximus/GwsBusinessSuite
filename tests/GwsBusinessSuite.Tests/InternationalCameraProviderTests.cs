using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// Fixtures are trimmed copies of the live responses captured 2026-10-08.
public sealed class InternationalCameraProviderTests
{
    private static readonly BoundingBox World = new(90, -90, 180, -180);

    [Fact]
    public async Task Iceland_ShouldMapCameras_SkippingBadCoordinatesAndDuplicateImages()
    {
        const string json = """
            [
              {"Maelist_nr":7001,"Myndavel":"Hellisheiði","Skyring":"Hellisheiði séð til vesturs","Slod":"https://www.vegagerdin.is/vgdata/vefmyndavelar/hellisheidi_1.jpg","Breidd":64.018296,"Lengd":-21.342636},
              {"Maelist_nr":7001,"Myndavel":"Hellisheiði","Skyring":"Duplicate","Slod":"https://www.vegagerdin.is/vgdata/vefmyndavelar/hellisheidi_1.jpg","Breidd":64.0,"Lengd":-21.3},
              {"Maelist_nr":7002,"Myndavel":"No location","Slod":"https://www.vegagerdin.is/vgdata/vefmyndavelar/x.jpg","Breidd":0,"Lengd":0},
              {"Maelist_nr":7003,"Myndavel":"Plain http","Slod":"http://example.is/y.jpg","Breidd":64.1,"Lengd":-21.0}
            ]
            """;
        var provider = new IcelandRoadCameraProvider(Client("https://gagnaveita.vegagerdin.is/", _ => Json(json)),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<IcelandRoadCameraProvider>.Instance);

        var cameras = await provider.GetCamerasAsync(World);

        cameras.Should().ContainSingle();
        cameras[0].Name.Should().Be("Iceland: Hellisheiði séð til vesturs");
        cameras[0].StreamKind.Should().Be(CameraStreamKind.Snapshot);
        cameras[0].Id.Should().Be("vegagerdin-7001-hellisheidi_1");
    }

    [Fact]
    public async Task Singapore_ShouldMapCameras_AndNotCallTheApiForViewsElsewhere()
    {
        const string json = """
            {"items":[{"timestamp":"2026-10-08T21:01:54+08:00","cameras":[
              {"timestamp":"2026-10-08T21:01:54+08:00","image":"https://images.data.gov.sg/api/traffic-images/2026/10/288fe7c4.jpg","location":{"longitude":103.823888890049,"latitude":1.26027777363278},"camera_id":"4799"}]}]}
            """;
        var calls = 0;
        var provider = new SingaporeLtaCameraProvider(Client("https://api.data.gov.sg/", _ => { calls++; return Json(json); }),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<SingaporeLtaCameraProvider>.Instance);

        (await provider.GetCamerasAsync(new BoundingBox(45, 30, -70, -90))).Should().BeEmpty();
        calls.Should().Be(0, "a view over the U.S. never needs Singapore's list");

        var cameras = await provider.GetCamerasAsync(new BoundingBox(1.5, 1.1, 104.1, 103.5));
        cameras.Should().ContainSingle().Which.Id.Should().Be("sg-lta-4799");
        calls.Should().Be(1);
    }

    [Fact]
    public void HongKong_ShouldDecodeUtf16_AndParseTheTabSeparatedList()
    {
        const string text = "key\tregion\tdistrict\tdescription\teasting\tnorthing\tlatitude\tlongitude\turl\r\n" +
                            "H429F\tHong Kong Island\tSouthern\tAberdeen Praya Road near Fish Market [H429F]\t833549.0\t812187.0\t22.24845\t114.1505\thttps://tdcctv.data.one.gov.hk/H429F.JPG\r\n" +
                            "BAD1\tX\tY\tNo coords\t0\t0\t\t\thttps://tdcctv.data.one.gov.hk/BAD1.JPG\r\n";
        // The live file carries the UTF-16 byte-order mark twice.
        var bytes = new byte[] { 0xFF, 0xFE, 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();

        var cameras = HongKongTdCameraProvider.Parse(HongKongTdCameraProvider.Decode(bytes));

        cameras.Should().ContainSingle();
        cameras[0].Should().Match<CameraFeed>(c => c.Id == "hk-td-H429F" && c.Latitude == 22.24845
            && c.StreamUrl == "https://tdcctv.data.one.gov.hk/H429F.JPG" && c.Name.StartsWith("Hong Kong: Aberdeen"));
    }

    [Fact]
    public async Task Providers_ShouldReturnEmpty_RatherThanThrow_WhenTheSourceFails()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        HttpResponseMessage Fail(HttpRequestMessage _) => new(HttpStatusCode.ServiceUnavailable);

        (await new IcelandRoadCameraProvider(Client("https://a/", Fail), cache, NullLogger<IcelandRoadCameraProvider>.Instance).GetCamerasAsync(World)).Should().BeEmpty();
        (await new SingaporeLtaCameraProvider(Client("https://a/", Fail), cache, NullLogger<SingaporeLtaCameraProvider>.Instance).GetCamerasAsync(World)).Should().BeEmpty();
        (await new HongKongTdCameraProvider(Client("https://a/", Fail), cache, NullLogger<HongKongTdCameraProvider>.Instance).GetCamerasAsync(World)).Should().BeEmpty();
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient Client(string baseAddress, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new Handler(respond)) { BaseAddress = new Uri(baseAddress) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
