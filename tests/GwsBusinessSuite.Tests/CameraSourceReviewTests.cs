using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// 19-11-26: camera sources' terms are drafted for Grant to confirm, and a source he switches off
// isn't asked for cameras.
public sealed class CameraSourceReviewTests
{
    private static readonly BoundingBox AnyBbox = new(90, -90, 180, -180);

    [Fact]
    public void Drafts_ShouldOnlyClaimWhatTheIntegrationNotesSay()
    {
        CameraSourceDrafts.For("Transport for NSW").Basis.Should().Contain("CC BY");
        CameraSourceDrafts.For("WSDOT").Basis.Should().Be(CameraSourceDrafts.NotRead);
        CameraSourceDrafts.For("WSDOT").TermsUrl.Should().StartWith("https://");
        CameraSourceDrafts.For("Some 511 region").Should().Be(new CameraSourceDrafts.Draft(CameraSourceDrafts.NotRead, null));
    }

    [Fact]
    public async Task Confirming_AndSwitchingOff_ShouldBeStored_AndASwitchedOffSourceSkipped()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var reviews = new CameraSourceReviewService(factory, NullLogger<CameraSourceReviewService>.Instance);

        await reviews.ConfirmAsync("WSDOT", "grant");
        await reviews.SetOffAsync("Madrid Traffic", true, "grant");

        var rows = await reviews.ListAsync(["WSDOT", "Madrid Traffic", "CDOT"]);
        rows.Select(r => r.SourceName).Should().Equal("CDOT", "Madrid Traffic", "WSDOT");
        rows.Single(r => r.SourceName == "WSDOT").ConfirmedBy.Should().Be("grant");
        rows.Single(r => r.SourceName == "Madrid Traffic").IsOff.Should().BeTrue();
        reviews.IsOff("Madrid Traffic").Should().BeTrue();

        var directory = new CameraDirectoryService(
            [new Provider("Madrid Traffic", [Camera("m", "Madrid Traffic")]), new Provider("WSDOT", [Camera("w", "WSDOT")])],
            NullLogger<CameraDirectoryService>.Instance, sourceSwitch: reviews);
        (await directory.GetCamerasInBoundingBoxAsync(AnyBbox)).Select(c => c.Id).Should().Equal("w");

        await reviews.SetOffAsync("Madrid Traffic", false, "grant");
        reviews.IsOff("Madrid Traffic").Should().BeFalse();
    }

    private static CameraFeed Camera(string id, string source) =>
        new(id, id, 1, 1, $"https://cams.example/{id}.jpg", CameraStreamKind.Snapshot, source, "https://cams.example");

    private sealed class Provider(string sourceName, IReadOnlyList<CameraFeed> cameras) : ICameraFeedProvider
    {
        public string SourceName => sourceName;
        public Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default) => Task.FromResult(cameras);
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
