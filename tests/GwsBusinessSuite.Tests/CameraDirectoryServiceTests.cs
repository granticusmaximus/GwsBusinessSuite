using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class CameraDirectoryServiceTests
{
    private static readonly BoundingBox AnyBbox = new(90, -90, 180, -180);

    [Fact]
    public async Task GetCamerasInBoundingBoxAsync_ShouldMergeResults_FromEveryProvider()
    {
        var one = new FakeCameraFeedProvider("One", [Camera("a", "One")]);
        var two = new FakeCameraFeedProvider("Two", [Camera("b", "Two"), Camera("c", "Two")]);
        var service = new CameraDirectoryService([one, two], NullLogger<CameraDirectoryService>.Instance);

        var result = await service.GetCamerasInBoundingBoxAsync(AnyBbox);

        result.Should().HaveCount(3);
        result.Select(c => c.Id).Should().BeEquivalentTo(["a", "b", "c"]);
    }

    [Fact]
    public async Task GetCamerasInBoundingBoxAsync_ShouldIsolateAFailingProvider_FromTheOthers()
    {
        var healthy = new FakeCameraFeedProvider("Healthy", [Camera("a", "Healthy")]);
        var broken = new ThrowingCameraFeedProvider("Broken");
        var service = new CameraDirectoryService([healthy, broken], NullLogger<CameraDirectoryService>.Instance);

        var result = await service.GetCamerasInBoundingBoxAsync(AnyBbox);

        result.Should().ContainSingle(c => c.Id == "a");
    }

    [Fact]
    public async Task ProvidersWithACoverageArea_ShouldOnlyBeAskedAboutViewsThatOverlapIt()
    {
        var hongKong = new FakeCameraFeedProvider("HK", [Camera("hk", "HK")]) { Coverage = new BoundingBox(22.6, 22.1, 114.5, 113.8) };
        var global = new FakeCameraFeedProvider("Global", [Camera("g", "Global")]);
        var service = new CameraDirectoryService([hongKong, global], NullLogger<CameraDirectoryService>.Instance);

        var overTexas = await service.GetCamerasInBoundingBoxAsync(new BoundingBox(36, 26, -93, -107));

        overTexas.Select(c => c.Id).Should().Equal("g");
        hongKong.Calls.Should().Be(0);
        (await service.GetCamerasInBoundingBoxAsync(new BoundingBox(23, 22, 115, 113))).Should().HaveCount(2);
    }

    [Fact]
    public async Task GetCamerasForViewAsync_ShouldThinAWideView_KeepingItsGeographicSpread()
    {
        // 2,500 cameras packed into one corner plus one camera alone in the opposite corner.
        var crowded = Enumerable.Range(0, 2500).Select(i => Camera($"c{i:0000}", "Crowd") with { Latitude = 10 + i % 50 * 0.01, Longitude = 10 + i / 50 * 0.01 });
        var lonely = Camera("lonely", "Crowd") with { Latitude = 80, Longitude = 170 };
        var service = new CameraDirectoryService([new FakeCameraFeedProvider("Crowd", [.. crowded, lonely])], NullLogger<CameraDirectoryService>.Instance);

        var view = await service.GetCamerasForViewAsync(AnyBbox, maxPins: 100);

        view.TotalInView.Should().Be(2501);
        view.IsThinned.Should().BeTrue();
        view.Cameras.Count.Should().BeLessThanOrEqualTo(100);
        view.Cameras.Should().Contain(c => c.Id == "lonely", "thinning keeps one camera per area, not the first N");
    }

    [Fact]
    public async Task GetCamerasForViewAsync_ShouldReturnEverything_WhenUnderTheCap_AndApplyTheFilter()
    {
        var service = new CameraDirectoryService([new FakeCameraFeedProvider("One", [Camera("a", "One"), Camera("b", "One")])], NullLogger<CameraDirectoryService>.Instance);

        var view = await service.GetCamerasForViewAsync(AnyBbox, include: c => c.Id != "b");

        view.Cameras.Select(c => c.Id).Should().Equal("a");
        view.TotalInView.Should().Be(1);
        view.IsThinned.Should().BeFalse();
    }

    [Fact]
    public void Intersects_ShouldDetectOverlapAndSeparation()
    {
        var georgia = new BoundingBox(35, 30.3, -80.8, -85.6);
        georgia.Intersects(new BoundingBox(34, 33, -84, -85)).Should().BeTrue("contained");
        georgia.Intersects(new BoundingBox(40, 34, -80, -82)).Should().BeTrue("corner overlap");
        georgia.Intersects(new BoundingBox(22.6, 22.1, 114.5, 113.8)).Should().BeFalse();
    }

    private static CameraFeed Camera(string id, string source) =>
        new(id, id, 0, 0, $"https://example.test/{id}.jpg", CameraStreamKind.Snapshot, source, "https://example.test");

    private sealed class FakeCameraFeedProvider(string sourceName, IReadOnlyList<CameraFeed> cameras) : ICameraFeedProvider
    {
        public string SourceName => sourceName;
        public BoundingBox? Coverage { get; init; }
        public int Calls { get; private set; }

        public Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(cameras);
        }
    }

    private sealed class ThrowingCameraFeedProvider(string sourceName) : ICameraFeedProvider
    {
        public string SourceName => sourceName;

        public Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated provider failure.");
    }
}
