using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class WestVirginia511CameraProviderTests
{
    // Shaped from live wv511.org/wsvc/gmap.asmx/buildCamerasJSONjs (2026-10-09): a JavaScript
    // file, with the camera object assigned to camera_data among other script.
    private const string CameraScript = """
        var styles = [[{ url: '/icons/people35.png', height: 35 }]];
        camera_data = { "count": 3,
         "cams": [
          {"origin":"Camera-1","md5":"CAM117","title":"I-81","description":"<div id=\"camDescription\">[BER]I-81 @ 0.5<span style=\"float:right\">West Virginia DOT</span></div><!--STREAMING:1-->","start_lat":"39.302863","start_lng":"-78.078892","ev_radius":null,"icon":"icon_feed"},
          {"origin":"Camera-2","md5":"CAM029","title":"I-81","description":"<div id=\"camDescription\">[BER]I-81 @ 13 King Street</div>","start_lat":"39.464","start_lng":"-77.9893","ev_radius":null,"icon":"icon_feed"},
          {"origin":"Camera-3","md5":"CAM404","title":"US-19","description":"","start_lat":"38.5","start_lng":"-80.9","ev_radius":null,"icon":"icon_feed"}
         ]};
        function showHideCameras2(onoff) { }
        """;

    private static readonly BoundingBox Everywhere = new(90, -90, 180, -180);

    [Fact]
    public async Task GetCamerasAsync_ShouldListCamerasWhoseManifestAnswers_WithTheirOwnHlsHost()
    {
        var provider = CreateProvider(Respond);

        var result = await provider.GetCamerasAsync(Everywhere);

        result.Select(c => c.Id).Should().BeEquivalentTo(["wv511-CAM117", "wv511-CAM029"]);
        var camera = result.Single(c => c.Id == "wv511-CAM117");
        camera.Name.Should().Be("WV511: I-81 @ 0.5");
        camera.Latitude.Should().Be(39.302863);
        camera.Longitude.Should().Be(-78.078892);
        camera.StreamKind.Should().Be(CameraStreamKind.Hls);
        camera.StreamUrl.Should().Be("https://vtc1.roadsummary.com/rtplive/CAM117/playlist.m3u8");
        result.Single(c => c.Id == "wv511-CAM029").StreamUrl.Should().Be("https://vtc3.roadsummary.com/rtplive/CAM029/playlist.m3u8");
    }

    [Fact]
    public async Task GetCamerasAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheListFails()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await provider.GetCamerasAsync(Everywhere);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url.EndsWith("buildCamerasJSONjs")) return Text(CameraScript);
        if (url.Contains("flowplayeri.aspx?CAMID=CAM117")) return Text("""<script>var src = "https://vtc1.roadsummary.com/rtplive/CAM117/playlist.m3u8";</script>""");
        if (url.Contains("flowplayeri.aspx?CAMID=CAM029")) return Text("""<source src='https://vtc3.roadsummary.com/rtplive/CAM029/playlist.m3u8' />""");
        if (url.Contains("flowplayeri.aspx?CAMID=CAM404")) return Text("""<source src='https://vtc2.roadsummary.com/rtplive/CAM404/playlist.m3u8' />""");
        if (url.EndsWith("/CAM404/playlist.m3u8")) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (url.EndsWith("playlist.m3u8")) return Text("#EXTM3U");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static WestVirginia511CameraProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        new(
            new HttpClient(new RecordingHandler(responseFactory)) { BaseAddress = new Uri("https://wv511.org/") },
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<WestVirginia511CameraProvider>.Instance);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
