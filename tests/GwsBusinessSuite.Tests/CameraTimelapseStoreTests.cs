using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class CameraTimelapseStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gws-timelapse-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task SaveFrame_ShouldListFramesOldestFirst_AndKeepOnlyADayPerCamera()
    {
        var store = new CameraTimelapseStore(_root, NullLogger<CameraTimelapseStore>.Instance);
        for (var i = 0; i < CameraTimelapseStore.MaxFramesPerCamera + 5; i++)
            await store.SaveFrameAsync("hk-td-H429F", [0xFF, 0xD8, 0xFF, (byte)i], T0.AddMinutes(10 * i));

        var frames = store.ListFrames("hk-td-H429F");

        frames.Should().HaveCount(CameraTimelapseStore.MaxFramesPerCamera);
        frames[0].CapturedAt.Should().Be(T0.AddMinutes(50), "the five oldest frames were dropped");
        frames.Select(f => f.CapturedAt).Should().BeInAscendingOrder();
        store.FramePath("hk-td-H429F", frames[0].FileName).Should().NotBeNull();
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("20261008T120000Z.jpg/../x")]
    [InlineData("notaframe.jpg")]
    public async Task FramePath_ShouldOnlyResolveExactFrameNames(string fileName)
    {
        var store = new CameraTimelapseStore(_root, NullLogger<CameraTimelapseStore>.Instance);
        await store.SaveFrameAsync("cam", [1, 2, 3], T0);

        store.FramePath("cam", fileName).Should().BeNull();
        store.FramePath("cam", "20261008T120000Z.jpg").Should().NotBeNull();
    }

    [Fact]
    public void FolderFor_ShouldKeepCameraIdsInsideTheRoot()
    {
        CameraTimelapseStore.FolderFor("../../evil/cam").Should().Be("______evil_cam");
    }

    [Fact]
    public async Task HealthCheck_ShouldHandOverAFrameOnlyWhenThePictureChanged()
    {
        var image = new byte[] { 0xFF, 0xD8, 0xFF, 1 };
        var clock = new Clock { UtcNow = T0 };
        var health = new CameraHealthService(new HttpClient(new Handler(() => image)), new MemoryCache(new MemoryCacheOptions()), clock,
            NullLogger<CameraHealthService>.Instance);
        var camera = new CameraFeed("cam", "Cam", 0, 0, "https://cams.example/1.jpg", CameraStreamKind.Snapshot, "Test", "https://cams.example");

        (await health.CheckWithFrameAsync(camera)).NewImage.Should().NotBeNull("the first frame is new");
        clock.UtcNow = T0.AddMinutes(10);
        (await health.CheckWithFrameAsync(camera)).NewImage.Should().BeNull("a frozen picture isn't stored twice");
        image = [0xFF, 0xD8, 0xFF, 2];
        clock.UtcNow = T0.AddMinutes(20);
        (await health.CheckWithFrameAsync(camera)).NewImage.Should().Equal(image);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Handler(Func<byte[]> image) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(image());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
