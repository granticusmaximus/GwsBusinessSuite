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

    private static CameraFeed Camera(string id, string source) =>
        new(id, id, 0, 0, $"https://example.test/{id}.jpg", CameraStreamKind.Snapshot, source, "https://example.test");

    private sealed class FakeCameraFeedProvider(string sourceName, IReadOnlyList<CameraFeed> cameras) : ICameraFeedProvider
    {
        public string SourceName => sourceName;

        public Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default) =>
            Task.FromResult(cameras);
    }

    private sealed class ThrowingCameraFeedProvider(string sourceName) : ICameraFeedProvider
    {
        public string SourceName => sourceName;

        public Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated provider failure.");
    }
}
