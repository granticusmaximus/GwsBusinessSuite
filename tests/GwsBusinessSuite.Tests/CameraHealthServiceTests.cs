using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class CameraHealthServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly CameraFeed Camera = new("cam-1", "Cam", 1, 1, "https://cams.example/1.jpg", CameraStreamKind.Snapshot, "Test", "https://cams.example");

    [Fact]
    public async Task SameImageForHalfAnHour_ShouldBeFrozen_AndANewImageShouldMakeItLiveAgain()
    {
        var clock = new Clock { UtcNow = T0 };
        var image = new byte[] { 1, 2, 3 };
        var service = Create(clock, _ => Image(image));

        (await service.CheckAsync(Camera)).Status.Should().Be(CameraHealthStatus.Unknown, "one check proves nothing");
        clock.UtcNow = T0.AddMinutes(15);
        (await service.CheckAsync(Camera)).Status.Should().Be(CameraHealthStatus.Unknown);
        clock.UtcNow = T0.AddMinutes(31);
        var frozen = await service.CheckAsync(Camera);
        frozen.Status.Should().Be(CameraHealthStatus.Frozen);
        frozen.Detail.Should().Contain("3 checks");
        service.GetKnown("cam-1")!.Status.Should().Be(CameraHealthStatus.Frozen);

        image = [4, 5, 6];
        clock.UtcNow = T0.AddMinutes(45);
        (await service.CheckAsync(Camera)).Status.Should().Be(CameraHealthStatus.Live);
    }

    [Fact]
    public async Task ChecksCloserThanNinetySeconds_ShouldReuseTheLastResult()
    {
        var clock = new Clock { UtcNow = T0 };
        var calls = 0;
        var service = Create(clock, _ => { calls++; return Image([1]); });

        await service.CheckAsync(Camera);
        clock.UtcNow = T0.AddSeconds(30);
        await service.CheckAsync(Camera);

        calls.Should().Be(1);
    }

    [Fact]
    public async Task AnOldLastModifiedHeader_ShouldBeStale_FromASingleCheck()
    {
        var service = Create(new Clock { UtcNow = T0 }, _ =>
        {
            var response = Image([1]);
            response.Content.Headers.LastModified = T0.AddHours(-5);
            return response;
        });

        var report = await service.CheckAsync(Camera);

        report.Status.Should().Be(CameraHealthStatus.Stale);
        report.Detail.Should().Contain("5 hours");
    }

    [Fact]
    public async Task AnHtmlPageOrRepeatedFailures_ShouldBeOffline()
    {
        var clock = new Clock { UtcNow = T0 };
        var service = Create(clock, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>app shell</html>", System.Text.Encoding.UTF8, "text/html") });

        (await service.CheckAsync(Camera)).Status.Should().Be(CameraHealthStatus.Offline, "a retired endpoint serving its web app is not an image");
    }

    [Fact]
    public void OneFailureAfterGoodChecks_ShouldNotYetBeOffline()
    {
        var samples = new List<CameraHealthService.Sample>
        {
            new(T0, true, "A", null),
            new(T0.AddMinutes(5), true, "B", null),
            new(T0.AddMinutes(10), false, null, null),
        };

        CameraHealthService.Evaluate("c", samples, T0.AddMinutes(10)).Status.Should().Be(CameraHealthStatus.Unknown);
        samples.Add(new(T0.AddMinutes(15), false, null, null));
        CameraHealthService.Evaluate("c", samples, T0.AddMinutes(15)).Status.Should().Be(CameraHealthStatus.Offline);
    }

    [Fact]
    public async Task HlsCameras_ShouldNotBeFetched()
    {
        var calls = 0;
        var service = Create(new Clock { UtcNow = T0 }, _ => { calls++; return Image([1]); });

        var report = await service.CheckAsync(Camera with { StreamKind = CameraStreamKind.Hls });

        report.Status.Should().Be(CameraHealthStatus.Unknown);
        calls.Should().Be(0);
    }

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static CameraHealthService Create(Clock clock, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(respond)), new MemoryCache(new MemoryCacheOptions()), clock, NullLogger<CameraHealthService>.Instance);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
