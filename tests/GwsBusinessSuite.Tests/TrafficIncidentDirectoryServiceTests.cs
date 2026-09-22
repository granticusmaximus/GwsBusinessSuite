using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class TrafficIncidentDirectoryServiceTests
{
    private static readonly BoundingBox AnyBbox = new(90, -90, 180, -180);

    [Fact]
    public async Task GetIncidentsInBoundingBoxAsync_ShouldMergeResults_FromEveryProvider()
    {
        var one = new FakeTrafficIncidentProvider("One", [Incident("a", "One")]);
        var two = new FakeTrafficIncidentProvider("Two", [Incident("b", "Two"), Incident("c", "Two")]);
        var service = new TrafficIncidentDirectoryService([one, two], NullLogger<TrafficIncidentDirectoryService>.Instance);

        var result = await service.GetIncidentsInBoundingBoxAsync(AnyBbox);

        result.Should().HaveCount(3);
        result.Select(i => i.Id).Should().BeEquivalentTo(["a", "b", "c"]);
    }

    [Fact]
    public async Task GetIncidentsInBoundingBoxAsync_ShouldIsolateAFailingProvider_FromTheOthers()
    {
        var healthy = new FakeTrafficIncidentProvider("Healthy", [Incident("a", "Healthy")]);
        var broken = new ThrowingTrafficIncidentProvider("Broken");
        var service = new TrafficIncidentDirectoryService([healthy, broken], NullLogger<TrafficIncidentDirectoryService>.Instance);

        var result = await service.GetIncidentsInBoundingBoxAsync(AnyBbox);

        result.Should().ContainSingle(i => i.Id == "a");
    }

    private static TrafficIncident Incident(string id, string source) =>
        new(id, "Test Road", "Test description", "accidentsAndIncidents", "minor", 0, 0, source, "https://example.test");

    private sealed class FakeTrafficIncidentProvider(string sourceName, IReadOnlyList<TrafficIncident> incidents) : ITrafficIncidentProvider
    {
        public string SourceName => sourceName;

        public Task<IReadOnlyList<TrafficIncident>> GetIncidentsAsync(BoundingBox bbox, CancellationToken cancellationToken = default) =>
            Task.FromResult(incidents);
    }

    private sealed class ThrowingTrafficIncidentProvider(string sourceName) : ITrafficIncidentProvider
    {
        public string SourceName => sourceName;

        public Task<IReadOnlyList<TrafficIncident>> GetIncidentsAsync(BoundingBox bbox, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated provider failure.");
    }
}
