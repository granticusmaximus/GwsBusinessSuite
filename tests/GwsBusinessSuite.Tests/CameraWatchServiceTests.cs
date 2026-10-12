using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class CameraWatchServiceTests : IAsyncDisposable
{
    private const string Ok = """{"road":"dry","visibility":"clear","traffic":"light","note":"","answer":""}""";
    private const string Stopped = """{"road":"dry","visibility":"clear","traffic":"stopped","note":"crash blocking lanes","answer":""}""";
    private const string Blank = "I can't see anything";

    private static readonly CameraFeed Favorite = new("fav-1", "I-75 at Exit 246", 33.75, -84.39, "https://cams.example/fav.jpg", CameraStreamKind.Snapshot, "GDOT", "https://511ga.org");
    private static readonly CameraFeed Video = new("hls-1", "Video cam", 33.75, -84.39, "https://cams.example/live.m3u8", CameraStreamKind.Hls, "GDOT", "https://511ga.org");
    private static readonly CameraFeed InArea = new("area-1", "GA 400 at Exit 3", 33.80, -84.37, "https://cams.example/area.jpg", CameraStreamKind.Snapshot, "GDOT", "https://511ga.org");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _frames = Path.Combine(Path.GetTempPath(), "camera-watch-tests-" + Guid.NewGuid().ToString("N"));
    private DbContextOptions<ApplicationDbContext> _options = null!;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly OverwatchAreaNotifier _notifier = new();

    private async Task<CameraWatchService> CreateAsync(bool withArea = true)
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        await using (var db = new ApplicationDbContext(_options))
        {
            await db.Database.EnsureCreatedAsync();
            if (withArea)
            {
                db.OverwatchWatchAreas.Add(new OverwatchWatchArea { Username = "grant", Name = "North", North = 34, South = 33.7, East = -84.3, West = -84.5 });
                await db.SaveChangesAsync();
            }
        }
        return new CameraWatchService(
            new Factory(_options),
            new Favorites([Favorite, Video]),
            new CameraDirectoryService([new Provider([InArea, Favorite])], NullLogger<CameraDirectoryService>.Instance),
            new CameraSnapshotDownloader(new HttpClient(new JpegHandler())),
            new CameraWatchFrameStore(_frames, NullLogger<CameraWatchFrameStore>.Instance),
            _notifier,
            new MemoryCache(new MemoryCacheOptions()),
            _clock,
            NullLogger<CameraWatchService>.Instance);
    }

    [Fact]
    public async Task WatchList_ShouldBeSnapshotFavoritesThenAreaCameras_WithoutDuplicates()
    {
        var service = await CreateAsync();

        (await service.GetWatchListAsync("grant")).Select(c => c.Id).Should().Equal("fav-1", "area-1");
        (await service.GetWatchListAsync("someone-else")).Select(c => c.Id).Should().Equal("fav-1");
    }

    [Fact]
    public async Task Reading_ShouldAlertOnceWhenACameraTurnsBad_WithBeforeAndAfterFrames()
    {
        var service = await CreateAsync();
        var raised = new List<OverwatchAreaAlertView>();
        _notifier.OnAlert += (_, alert) => raised.Add(alert);

        (await service.RecordReadingAsync("grant", "fav-1", Ok, [1, 1, 1]))!.Alerted.Should().BeFalse();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var bad = await service.RecordReadingAsync("grant", "fav-1", Stopped, [2, 2, 2]);
        _clock.Advance(TimeSpan.FromMinutes(10));
        var stillBad = await service.RecordReadingAsync("grant", "fav-1", Stopped, [3, 3, 3]);

        bad!.Alerted.Should().BeTrue();
        bad.Summary.Should().Be("traffic stopped - crash blocking lanes");
        stillBad!.Alerted.Should().BeFalse();
        raised.Should().ContainSingle();
        await using var db = new ApplicationDbContext(_options);
        var alert = await db.OverwatchAreaAlerts.SingleAsync();
        alert.Title.Should().Be("Camera watch: I-75 at Exit 246");
        alert.Message.Should().Be("Now traffic stopped - crash blocking lanes (was ok).");
        alert.LinkUrl.Should().Be($"/admin/osint?detection={alert.Id}");
        alert.CameraId.Should().Be("fav-1");
        var frames = new CameraWatchFrameStore(_frames, NullLogger<CameraWatchFrameStore>.Instance);
        frames.ReadAlertFrame(alert.Id, "before").Should().Equal(1, 1, 1);
        frames.ReadAlertFrame(alert.Id, "after").Should().Equal(2, 2, 2);
        frames.ReadAlertFrame(alert.Id, "../secret").Should().BeNull();
    }

    [Fact]
    public async Task Reading_ShouldNotAlertAgainWithinTwoHours_ButShouldAfter()
    {
        var service = await CreateAsync();
        await service.RecordReadingAsync("grant", "fav-1", Ok, [1]);
        (await service.RecordReadingAsync("grant", "fav-1", Stopped, [2]))!.Alerted.Should().BeTrue();
        await service.RecordReadingAsync("grant", "fav-1", Ok, [3]);
        _clock.Advance(TimeSpan.FromMinutes(30));
        (await service.RecordReadingAsync("grant", "fav-1", Stopped, [4]))!.Alerted.Should().BeFalse("it flapped within the cooldown");
        await service.RecordReadingAsync("grant", "fav-1", Ok, [5]);
        _clock.Advance(TimeSpan.FromHours(2));
        (await service.RecordReadingAsync("grant", "fav-1", Stopped, [6]))!.Alerted.Should().BeTrue();
    }

    [Fact]
    public async Task Reading_ShouldIgnoreUnreadableFrames_AndCamerasNotOnTheList()
    {
        var service = await CreateAsync();
        await service.RecordReadingAsync("grant", "fav-1", Ok, [1]);
        (await service.RecordReadingAsync("grant", "fav-1", Blank, [2]))!.Level.Should().Be("unknown");
        (await service.RecordReadingAsync("grant", "fav-1", Stopped, [3]))!.Alerted.Should().BeTrue("the last real reading was still ok");

        (await service.RecordReadingAsync("grant", "not-mine", Stopped, [4])).Should().BeNull();
        (await service.GetSnapshotAsync("grant", "not-mine")).Should().BeNull();
        (await service.GetSnapshotAsync("grant", "area-1")).Should().NotBeNull();
    }

    [Fact]
    public async Task StatusAndDetections_ShouldBelongToTheirOwner()
    {
        var service = await CreateAsync();
        await service.RecordReadingAsync("grant", "fav-1", Ok, [1]);
        await service.RecordReadingAsync("grant", "fav-1", Stopped, [2]);
        await using var db = new ApplicationDbContext(_options);
        var alertId = (await db.OverwatchAreaAlerts.SingleAsync()).Id;

        var status = await service.GetStatusAsync("grant");
        status.LastCheckedAt.Should().Be(_clock.GetUtcNow());
        status.Watched.Should().Be(2);
        status.NeedAttention.Should().ContainSingle(s => s.CameraId == "fav-1" && s.LastLevel == "bad");
        (await service.GetDetectionAsync("grant", alertId))!.Value.Camera!.CameraName.Should().Be("I-75 at Exit 246");
        (await service.GetDetectionAsync("someone-else", alertId)).Should().BeNull();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        if (Directory.Exists(_frames)) Directory.Delete(_frames, true);
    }

    private sealed class Favorites(IReadOnlyList<CameraFeed> cameras) : ICameraFavoritesService
    {
        public Task<IReadOnlyList<CameraFeed>> GetFavoritesAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult(cameras);
        public Task<bool> IsFavoriteAsync(string username, string cameraId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddFavoriteAsync(string username, CameraFeed camera, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFavoriteAsync(string username, string cameraId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Provider(IReadOnlyList<CameraFeed> cameras) : ICameraFeedProvider
    {
        public string SourceName => "Test";
        public Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CameraFeed>>(cameras.Where(c => bbox.Contains(c.Latitude, c.Longitude)).ToList());
    }

    private sealed class JpegHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2]) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }
}
