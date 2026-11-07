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
        (await new EstoniaRoadCameraProvider(Client("https://a/", Fail), cache, NullLogger<EstoniaRoadCameraProvider>.Instance).GetCamerasAsync(World)).Should().BeEmpty();
        (await new NorwayRoadCameraProvider(Client("https://a/", Fail), cache, NullLogger<NorwayRoadCameraProvider>.Instance).GetCamerasAsync(World)).Should().BeEmpty();
        (await new TflJamCamProvider(Client("https://a/", Fail), cache, NullLogger<TflJamCamProvider>.Instance).GetCamerasAsync(World)).Should().BeEmpty();
        (await new TorontoTrafficCameraProvider(Client("https://a/", Fail), cache, NullLogger<TorontoTrafficCameraProvider>.Instance).GetCamerasAsync(World)).Should().BeEmpty();
        (await new CalgaryTrafficCameraProvider(Client("https://a/", Fail), cache, NullLogger<CalgaryTrafficCameraProvider>.Instance).GetCamerasAsync(World)).Should().BeEmpty();
        (await new LuxembourgCitaCameraProvider(Client("https://a/", Fail), cache, NullLogger<LuxembourgCitaCameraProvider>.Instance).GetCamerasAsync(World)).Should().BeEmpty();
        // A malformed answer is a failure too, not an exception.
        (await new LuxembourgCitaCameraProvider(Client("https://a/", _ => Json("<kml><Placemark>")), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<LuxembourgCitaCameraProvider>.Instance).GetCamerasAsync(World)).Should().BeEmpty();
    }

    // Fixtures below are trimmed copies of the live responses captured 07-11-26.
    [Fact]
    public async Task London_ShouldMapTflJamCams_SkippingSwitchedOffOnes()
    {
        const string json = """
            [
              {"id":"JamCams_00002.00865","commonName":"A406 Billet Upass E","lat":51.60067,"lon":-0.01594,"additionalProperties":[
                {"key":"available","value":"true"},{"key":"imageUrl","value":"https://s3-eu-west-1.amazonaws.com/jamcams.tfl.gov.uk/00002.00865.jpg"},
                {"key":"videoUrl","value":"https://s3-eu-west-1.amazonaws.com/jamcams.tfl.gov.uk/00002.00865.mp4"},{"key":"view","value":"West"}]},
              {"id":"JamCams_00001.01251","commonName":"Switched off","lat":51.5,"lon":-0.1,"additionalProperties":[
                {"key":"available","value":"false"},{"key":"imageUrl","value":"https://s3-eu-west-1.amazonaws.com/jamcams.tfl.gov.uk/00001.01251.jpg"}]},
              {"id":"JamCams_00001.09999","commonName":"No image","lat":51.5,"lon":-0.1,"additionalProperties":[{"key":"available","value":"true"}]}
            ]
            """;
        var provider = new TflJamCamProvider(Client("https://api.tfl.gov.uk/", request =>
            request.RequestUri!.AbsolutePath == "/Place/Type/JamCam" ? Json(json) : new HttpResponseMessage(HttpStatusCode.NotFound)),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<TflJamCamProvider>.Instance);

        var cameras = await provider.GetCamerasAsync(World);

        cameras.Should().ContainSingle().Which.Should().Match<CameraFeed>(camera =>
            camera.Id == "tfl-00002.00865"
            && camera.Name == "London: A406 Billet Upass E (West)"
            && camera.StreamUrl == "https://s3-eu-west-1.amazonaws.com/jamcams.tfl.gov.uk/00002.00865.jpg"
            && camera.StreamKind == CameraStreamKind.Snapshot);
        provider.Coverage!.Contains(51.5072, -0.1276).Should().BeTrue("central London");
    }

    [Fact]
    public async Task Toronto_ShouldMapTheOpenDataGeoJson_WithReadableNames()
    {
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"_id":1,"REC_ID":8001,"IMAGEURL":"https://opendata.toronto.ca/transportation/tmc/rescucameraimages/CameraImages/loc8001.jpg",
                "MAINROAD":"YORK ST","CROSSROAD":"BREMNER BLVD / RAPTORS WAY"},"geometry":{"type":"MultiPoint","coordinates":[[-79.3813860009273,43.6431200069527]]}},
              {"type":"Feature","properties":{"REC_ID":8002,"IMAGEURL":"http://opendata.toronto.ca/insecure.jpg","MAINROAD":"KING ST"},"geometry":{"type":"Point","coordinates":[-79.38,43.64]}},
              {"type":"Feature","properties":{"REC_ID":8003,"IMAGEURL":"https://opendata.toronto.ca/loc8003.jpg","MAINROAD":"NOWHERE"},"geometry":{"type":"Point","coordinates":[0,0]}}
            ]}
            """;
        var provider = new TorontoTrafficCameraProvider(Client("https://ckan0.cf.opendata.inter.prod-toronto.ca/", request =>
            request.RequestUri!.AbsolutePath == "/" + TorontoTrafficCameraProvider.ListPath ? Json(json) : new HttpResponseMessage(HttpStatusCode.NotFound)),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<TorontoTrafficCameraProvider>.Instance);

        var cameras = await provider.GetCamerasAsync(World);

        cameras.Should().ContainSingle().Which.Should().Match<CameraFeed>(camera =>
            camera.Id == "toronto-8001"
            && camera.Name == "Toronto: York St at Bremner Blvd / Raptors Way"
            && Math.Abs(camera.Latitude - 43.64312) < 0.0001 && Math.Abs(camera.Longitude + 79.38139) < 0.0001);
    }

    [Fact]
    public async Task Calgary_ShouldMapTheOpenDataCameras()
    {
        const string json = """
            [
              {"camera_url":{"url":"https://trafficcam.calgary.ca/loc0.jpg","description":"Camera 1"},"quadrant":"SW/SE","camera_location":"Glenmore Trail / Macleod Trail S",
               "point":{"type":"Point","coordinates":[-114.0715855,50.993989]}},
              {"camera_url":{"url":"https://trafficcam.calgary.ca/loc1.jpg"},"camera_location":"No point"},
              {"camera_url":{"url":"javascript:alert(1)"},"camera_location":"Bad link","point":{"type":"Point","coordinates":[-114.07,50.99]}}
            ]
            """;
        var provider = new CalgaryTrafficCameraProvider(Client("https://data.calgary.ca/", request =>
            request.RequestUri!.AbsolutePath == "/resource/k7p9-kppz.json" ? Json(json) : new HttpResponseMessage(HttpStatusCode.NotFound)),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<CalgaryTrafficCameraProvider>.Instance);

        var cameras = await provider.GetCamerasAsync(World);

        cameras.Should().ContainSingle().Which.Should().Match<CameraFeed>(camera =>
            camera.Id == "calgary-loc0"
            && camera.Name == "Calgary: Glenmore Trail / Macleod Trail S (SW/SE)"
            && camera.StreamUrl == "https://trafficcam.calgary.ca/loc0.jpg");
    }

    [Fact]
    public async Task Luxembourg_ShouldMapCitaPlacemarks_ToTheirViewersCurrentJpeg()
    {
        const string kml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
              <Placemark id="camera_3">
                <name>A6 - Camera 3</name>
                <description><![CDATA[<iframe class="cam-frame" src="https://www.cita.lu/webcam/map/3" height="245" width="270" scrolling="no" ></iframe>]]></description>
                <Point><coordinates>5.921276000000001,49.63694099999999,0</coordinates></Point>
              </Placemark>
              <Placemark id="camera_x">
                <name>No viewer</name>
                <description>Something else</description>
                <Point><coordinates>6.0,49.6,0</coordinates></Point>
              </Placemark>
            </Document></kml>
            """;
        var provider = new LuxembourgCitaCameraProvider(Client("https://www.cita.lu/", request =>
            request.RequestUri!.AbsolutePath == "/kml/cameras.kml" ? Json(kml) : new HttpResponseMessage(HttpStatusCode.NotFound)),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<LuxembourgCitaCameraProvider>.Instance);

        var cameras = await provider.GetCamerasAsync(World);

        cameras.Should().ContainSingle().Which.Should().Match<CameraFeed>(camera =>
            camera.Id == "cita-3"
            && camera.Name == "Luxembourg: A6 - Camera 3"
            && camera.StreamUrl == "https://www.cita.lu/info_trafic/cameras/images/cccam_3.jpg"
            && Math.Abs(camera.Latitude - 49.636941) < 0.0001);
    }

    [Fact]
    public async Task Estonia_ShouldMapSafeCurrentJpegPaths()
    {
        const string json = """
            {"features":[
              {"attributes":{"objectid":11,"site_name":"Jõhvi","image_path":"94/94_202610081731.jpg"},"geometry":{"x":27.44067,"y":59.36536}},
              {"attributes":{"objectid":12,"site_name":"Unsafe","image_path":"../secret.jpg"},"geometry":{"x":25.0,"y":58.5}}
            ]}
            """;
        var provider = new EstoniaRoadCameraProvider(Client("https://tarktee.ee/", _ => Json(json)),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<EstoniaRoadCameraProvider>.Instance);

        var cameras = await provider.GetCamerasAsync(World);

        cameras.Should().ContainSingle().Which.Should().Match<CameraFeed>(camera =>
            camera.Id == "estonia-tarktee-11"
            && camera.Name == "Estonia: Jõhvi"
            && camera.StreamUrl == "https://tarktee.ee/images/94/94_202610081731.jpg");
    }

    [Fact]
    public async Task Norway_ShouldExposeOnlyWorkingPublicHlsCameras()
    {
        const string json = """
            {"measurementSites":[{"id":"2000065","name":"Aisaroaivi","location":{"geometry":{"type":"Point","coordinates":[24.10096,70.27835]}},"cameras":[
              {"id":"2000065_1","orientationDescription":"Sennalandet","stillImageUrl":"https://kamera.atlas.vegvesen.no/api/images/2000065_1","videoUrl":"https://kamera.vegvesen.no/public/2000065_1/manifest.m3u8","status":"OK","lastUpdated":"2026-10-08T14:51:16+02:00"},
              {"id":"2000065_2","orientationDescription":"Skaidi","stillImageUrl":"https://kamera.atlas.vegvesen.no/api/images/2000065_2","videoUrl":"","status":"OK","lastUpdated":"2026-10-08T14:51:16+02:00"}
            ]}],"metadata":{}}
            """;
        var provider = new NorwayRoadCameraProvider(Client("https://road-weather-and-view.atlas.vegvesen.no/", _ => Json(json)),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<NorwayRoadCameraProvider>.Instance);

        var cameras = await provider.GetCamerasAsync(World);

        cameras.Should().ContainSingle().Which.Should().Match<CameraFeed>(camera =>
            camera.Id == "norway-vegvesen-2000065_1"
            && camera.StreamKind == CameraStreamKind.Hls
            && camera.Name.Contains("Sennalandet"));
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
