using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class TimeMachineServiceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 0, 0, TimeSpan.Zero);
    private static readonly BoundingBox Atlanta = new(34.0, 33.5, -84.0, -84.7);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _frames = Path.Combine(Path.GetTempPath(), "time-machine-tests-" + Guid.NewGuid().ToString("N"));
    private DbContextOptions<ApplicationDbContext> _options = null!;
    private readonly Clock _clock = new(Now);

    [Theory]
    [InlineData(-120, "ridge::USCOMP-N0Q-202610111600")]
    [InlineData(-123, "ridge::USCOMP-N0Q-202610111555")]
    [InlineData(-5, "nexrad-n0q-900913")]
    public void RadarTileUrl_ShouldUseTheArchivedFiveMinuteSlot_OrTheLiveMosaicForTheLastMinutes(int minutes, string layer)
    {
        TimeMachineService.RadarTileUrl(Now.AddMinutes(minutes), Now)
            .Should().Be($"https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/{layer}/{{z}}/{{x}}/{{y}}.png");
    }

    [Fact]
    public async Task Recorder_ShouldKeepOnlyCrashesClosuresAndHazards_ExtendLastSeen_AndForgetAfter48Hours()
    {
        var factory = await CreateDbAsync();
        var recorder = new IncidentHistoryRecorder(_clock);
        var directory = new TrafficIncidentDirectoryService([new Incidents([Incident("c1", IncidentCategories.Crash), Incident("r1", IncidentCategories.Roadwork)])],
            NullLogger<TrafficIncidentDirectoryService>.Instance, history: recorder);

        await directory.GetIncidentsInBoundingBoxAsync(Atlanta);
        await IncidentHistoryFlushService.FlushAsync(factory, recorder, _clock, default);
        _clock.Now = Now.AddMinutes(30);
        await directory.GetIncidentsInBoundingBoxAsync(Atlanta);
        await IncidentHistoryFlushService.FlushAsync(factory, recorder, _clock, default);

        await using (var db = new ApplicationDbContext(_options))
        {
            var row = await db.OverwatchIncidentSightings.SingleAsync();
            row.Key.Should().Be("GDOT:c1");
            row.FirstSeenUnix.Should().Be(Now.ToUnixTimeSeconds());
            row.LastSeenUnix.Should().Be(Now.AddMinutes(30).ToUnixTimeSeconds());
        }

        _clock.Now = Now.AddHours(49);
        await IncidentHistoryFlushService.FlushAsync(factory, recorder, _clock, default);
        await using (var db = new ApplicationDbContext(_options))
        {
            (await db.OverwatchIncidentSightings.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Snapshot_ShouldShowIncidentsActiveThenInView_AndTheNearestCameraFrame()
    {
        var factory = await CreateDbAsync();
        await using (var db = new ApplicationDbContext(_options))
        {
            db.OverwatchIncidentSightings.AddRange(
                Sighting("early", Now.AddHours(-5), Now.AddHours(-4), 33.75, -84.39),
                Sighting("during", Now.AddHours(-3), Now.AddHours(-1), 33.75, -84.39),
                Sighting("elsewhere", Now.AddHours(-3), Now.AddHours(-1), 40.0, -100.0));
            await db.SaveChangesAsync();
        }
        var frames = new CameraTimelapseStore(_frames, NullLogger<CameraTimelapseStore>.Instance);
        await frames.SaveFrameAsync("fav-1", [0xFF, 0xD8, 1], Now.AddHours(-2).AddMinutes(-5));
        await frames.SaveFrameAsync("fav-1", [0xFF, 0xD8, 2], Now.AddMinutes(-30));
        var service = new TimeMachineService(factory,
            new HazardLayerService(new HttpClient(new Unavailable()), new MemoryCache(new MemoryCacheOptions()), NullLogger<HazardLayerService>.Instance),
            new Favorites([new CameraFeed("fav-1", "I-75", 33.75, -84.39, "https://cams.example/1.jpg", CameraStreamKind.Snapshot, "GDOT", "https://511ga.org"),
                new CameraFeed("fav-2", "No frames", 33.75, -84.39, "https://cams.example/2.jpg", CameraStreamKind.Snapshot, "GDOT", "https://511ga.org")]),
            frames, _clock);

        var snapshot = await service.GetAsync("grant", Atlanta, Now.AddHours(-2));

        snapshot.Incidents.Select(i => i.Key).Should().Equal("during");
        snapshot.Cameras.Should().ContainSingle().Which.FrameUrl.Should().EndWith("/fav-1/20261011T155500Z.jpg");
        snapshot.RadarTileUrl.Should().Contain("ridge::USCOMP-N0Q-202610111600");
        snapshot.IncidentHistoryStartsAt.Should().Be(Now.AddHours(-5));

        (await service.GetAsync("grant", Atlanta, Now.AddDays(-3))).At.Should().Be(Now.AddHours(-24), "the window is the past 24 hours");
    }

    private static TrafficIncident Incident(string id, string category) =>
        new(id, "I 75", "Something", category, "major", 33.75, -84.39, "GDOT", "https://511ga.org", Category: category);

    private static OverwatchIncidentSighting Sighting(string key, DateTimeOffset first, DateTimeOffset last, double lat, double lon) => new()
    {
        Key = key, Category = IncidentCategories.Crash, Latitude = lat, Longitude = lon,
        FirstSeenUnix = first.ToUnixTimeSeconds(), LastSeenUnix = last.ToUnixTimeSeconds()
    };

    private async Task<IAppDbContextFactory> CreateDbAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        await using (var db = new ApplicationDbContext(_options)) await db.Database.EnsureCreatedAsync();
        return new Factory(_options);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        if (Directory.Exists(_frames)) Directory.Delete(_frames, true);
    }

    private sealed class Incidents(IReadOnlyList<TrafficIncident> incidents) : ITrafficIncidentProvider
    {
        public string SourceName => "GDOT";
        public Task<IReadOnlyList<TrafficIncident>> GetIncidentsAsync(BoundingBox bbox, CancellationToken cancellationToken = default) => Task.FromResult(incidents);
    }

    private sealed class Favorites(IReadOnlyList<CameraFeed> cameras) : ICameraFavoritesService
    {
        public Task<IReadOnlyList<CameraFeed>> GetFavoritesAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult(cameras);
        public Task<bool> IsFavoriteAsync(string username, string cameraId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task AddFavoriteAsync(string username, CameraFeed camera, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFavoriteAsync(string username, string cameraId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Unavailable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }
}
