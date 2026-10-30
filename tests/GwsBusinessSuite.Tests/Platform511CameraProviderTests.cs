using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class Platform511CameraProviderTests
{
    private static readonly BoundingBox Alaska = new(North: 68.6, South: 54.5, East: -130.0, West: -170.0);

    [Fact]
    public async Task GetCameras_ShouldPageThroughTheKeylessListAndKeepEachLiveView()
    {
        var requests = new List<HttpRequestMessage>();
        var provider = Create(request =>
        {
            requests.Add(request);
            var start = Uri.UnescapeDataString(request.RequestUri!.Query).Contains("\"start\":100") ? 100 : 0;
            return Json(start == 0
                ? Page(101, Site(1, "Seward Highway MP 78.9", "POINT (-148.975515 60.818883)", Image(1, "Northbound"), Image(2, "Southbound")),
                    Site(2, "Nowhere", "POINT (0 0)", Image(3, "N/A")),
                    Site(3, "Dalton Highway", "POINT (-149.5 66.1)", Image(4, "N/A", disabled: true)))
                : Page(101, Site(4, "Glenn Highway", "POINT (-149.1 61.6)", Image(5, "N/A"))));
        });

        var cameras = await provider.GetCamerasAsync(Alaska);

        cameras.Select(c => (c.Name, c.StreamUrl)).Should().Equal(
            ("511 Alaska: Seward Highway MP 78.9 - Northbound", "https://511.alaska.gov/map/Cctv/1"),
            ("511 Alaska: Seward Highway MP 78.9 - Southbound", "https://511.alaska.gov/map/Cctv/2"),
            ("511 Alaska: Glenn Highway", "https://511.alaska.gov/map/Cctv/5"));
        cameras.Should().OnlyContain(c => c.StreamKind == CameraStreamKind.Snapshot && c.SourceName == "511 Alaska");
        cameras[0].Latitude.Should().BeApproximately(60.818883, 1e-6);
        requests.Should().HaveCount(2, "101 cameras is two pages of 100");
        requests.Should().OnlyContain(r => r.Headers.GetValues("X-Requested-With").Single() == "XMLHttpRequest");

        await provider.GetCamerasAsync(Alaska);
        requests.Should().HaveCount(2, "the list is cached");
        (await provider.GetCamerasAsync(new BoundingBox(61.0, 60.0, -148.0, -150.0))).Should().HaveCount(2, "only the view's cameras");
    }

    [Fact]
    public async Task GetCameras_ShouldShareOneDownloadBetweenViews_AndFetchThePagesAfterTheFirstTogether()
    {
        var requests = 0;
        var provider = Create(request =>
        {
            Interlocked.Increment(ref requests);
            var page = StartOf(request) / 100;
            return Json(Page(350, Site(page + 1, $"Page {page + 1}", "POINT (-149.1 61.6)", Image(page + 1, "N/A"))));
        });

        var views = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => provider.GetCamerasAsync(Alaska)));

        requests.Should().Be(4, "350 cameras is four pages, downloaded once for all three views");
        views.Should().OnlyContain(cameras => cameras.Count == 4);
        views[0].Select(c => c.Name).Should().Equal("511 Alaska: Page 1", "511 Alaska: Page 2", "511 Alaska: Page 3", "511 Alaska: Page 4");
    }

    [Fact]
    public async Task GetCameras_ShouldKeepServingAnOldList_WhileAFreshOneDownloads()
    {
        var clock = new ManualClock();
        var name = "Seward Highway";
        var provider = Create(_ => Json(Page(1, Site(1, name, "POINT (-149.1 61.6)", Image(1, "N/A")))), clock);
        (await provider.GetCamerasAsync(Alaska)).Single().Name.Should().Be("511 Alaska: Seward Highway");

        name = "Seward Highway (renamed)";
        clock.Advance(Platform511CameraProvider.FreshFor + TimeSpan.FromMinutes(1));
        (await provider.GetCamerasAsync(Alaska)).Single().Name.Should().Be("511 Alaska: Seward Highway", "the old list answers at once");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await provider.GetCamerasAsync(Alaska)).Single().Name != "511 Alaska: Seward Highway (renamed)" && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        (await provider.GetCamerasAsync(Alaska)).Single().Name.Should().Be("511 Alaska: Seward Highway (renamed)", "the refresh landed behind it");
    }

    [Theory]
    [InlineData("POINT (-148.975515 60.818883)", 60.818883, -148.975515)]
    [InlineData("POINT(-75.9 40.3)", 40.3, -75.9)]
    public void Point_ShouldReadLongitudeThenLatitude(string wkt, double lat, double lon)
    {
        Platform511CameraProvider.Point(wkt).Should().Be((lat, lon));
    }

    [Fact]
    public void Point_ShouldRejectMissingOrZeroLocations()
    {
        Platform511CameraProvider.Point("POINT (0 0)").Should().BeNull();
        Platform511CameraProvider.Point(null).Should().BeNull();
    }

    private static Platform511CameraProvider Create(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeProvider? time = null) =>
        new(new HttpClient(new Handler(respond)), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<Platform511CameraProvider>.Instance, "511.alaska.gov", "511 Alaska", Alaska, time);

    private static int StartOf(HttpRequestMessage request) =>
        int.Parse(System.Text.RegularExpressions.Regex.Match(Uri.UnescapeDataString(request.RequestUri!.Query), "\"start\":(\\d+)").Groups[1].Value);

    private static string Page(int total, params string[] sites) =>
        "{\"recordsTotal\":" + total + ",\"data\":[" + string.Join(",", sites) + "]}";

    private static string Site(int id, string location, string wkt, params string[] images) =>
        "{\"id\":" + id + ",\"roadway\":\"Road\",\"location\":\"" + location + "\",\"latLng\":{\"geography\":{\"wellKnownText\":\"" + wkt +
        "\"}},\"images\":[" + string.Join(",", images) + "]}";

    private static string Image(int id, string description, bool disabled = false) =>
        "{\"id\":" + id + ",\"description\":\"" + description + "\",\"imageUrl\":\"/map/Cctv/" + id + "\",\"disabled\":" +
        (disabled ? "true" : "false") + ",\"blocked\":false}";

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
