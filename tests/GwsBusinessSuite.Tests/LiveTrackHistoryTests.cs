using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.LiveLayers;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// #6: live-layer history for the time machine: thinned on the way in, read back for a moment.
public sealed class LiveTrackHistoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "live-history-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    private static readonly BoundingBox Atlanta = new(34.2, 33.4, -83.9, -84.9);

    private LiveTrackHistoryStore Store() => new(Path.Combine(_folder, "live-history.db"), _time, NullLogger<LiveTrackHistoryStore>.Instance);

    private LiveTrack Plane(string id, double lat, double lon) => new(LiveTrackKinds.Aircraft, id, "DAL" + id, lat, lon, 90, _time.GetUtcNow());

    [Fact]
    public void Record_ShouldKeepOneSamplePerObjectEveryFiveMinutes_AndAtShouldReturnTheLatestInView()
    {
        var store = Store();
        store.Record(LiveTrackKinds.Aircraft, [Plane("1", 33.7, -84.4), Plane("far", 40.7, -74.0)]);
        _time.Advance(TimeSpan.FromMinutes(2));
        store.Record(LiveTrackKinds.Aircraft, [Plane("1", 33.9, -84.1)]);        // too soon: thinned away
        _time.Advance(TimeSpan.FromMinutes(4));
        store.Record(LiveTrackKinds.Aircraft, [Plane("1", 34.0, -84.0)]);

        var now = store.At(Atlanta, _time.GetUtcNow());
        var earlier = store.At(Atlanta, _time.GetUtcNow().AddMinutes(-5));

        var plane = Assert.Single(now);
        Assert.Equal(34.0, plane.Latitude);
        Assert.Equal(33.7, Assert.Single(earlier).Latitude);
    }

    [Fact]
    public void At_ShouldDropSamplesOlderThanTheKindsTolerance()
    {
        var store = Store();
        store.Record(LiveTrackKinds.Aircraft, [Plane("1", 33.7, -84.4)]);
        store.Record(LiveTrackKinds.Ship, [new LiveTrack(LiveTrackKinds.Ship, "366", "TUG", 33.8, -84.3, null, _time.GetUtcNow())]);

        var later = store.At(Atlanta, _time.GetUtcNow().AddMinutes(20));

        Assert.Equal([LiveTrackKinds.Ship], later.Select(t => t.Kind));
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
