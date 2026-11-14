using System.Collections.Concurrent;
using System.Globalization;
using GwsBusinessSuite.Application.LiveLayers;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// #6: live-layer history in its own small SQLite file (live-history.db beside the app's data), so
// the main database never carries high-churn positions. Samples are thinned on the way in (one per
// object per SampleEvery) and rows past Retention are dropped every ten minutes.
public sealed class LiveTrackHistoryStore : ILiveTrackHistory
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly ILogger<LiveTrackHistoryStore> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSample = new(StringComparer.Ordinal);
    private readonly object _writes = new();
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public LiveTrackHistoryStore(string path, TimeProvider time, ILogger<LiveTrackHistoryStore> logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        _time = time;
        _logger = logger;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS tracks (
                kind TEXT NOT NULL, id TEXT NOT NULL, title TEXT NOT NULL, lat REAL NOT NULL, lon REAL NOT NULL, heading REAL, at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_tracks_kind_at ON tracks (kind, at);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Record(string kind, IEnumerable<LiveTrack> tracks)
    {
        var now = _time.GetUtcNow();
        var every = LiveTrackKinds.SampleEvery(kind);
        var due = tracks.Where(track => double.IsFinite(track.Latitude) && double.IsFinite(track.Longitude))
            .Where(track => !_lastSample.TryGetValue(kind + ":" + track.Id, out var last) || now - last >= every)
            .Take(5000).ToList();
        if (due.Count == 0) return;
        try
        {
            lock (_writes)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO tracks (kind, id, title, lat, lon, heading, at) VALUES ($kind, $id, $title, $lat, $lon, $heading, $at)";
                var p = new[] { "$kind", "$id", "$title", "$lat", "$lon", "$heading", "$at" }.Select(name => command.Parameters.Add(new SqliteParameter { ParameterName = name })).ToArray();
                foreach (var track in due)
                {
                    p[0].Value = kind; p[1].Value = track.Id; p[2].Value = track.Title; p[3].Value = track.Latitude; p[4].Value = track.Longitude;
                    p[5].Value = track.Heading is { } heading && double.IsFinite(heading) ? heading : DBNull.Value;
                    p[6].Value = track.At.ToUnixTimeSeconds();
                    command.ExecuteNonQuery();
                    _lastSample[kind + ":" + track.Id] = now;
                }
                if (now - _lastPrune > TimeSpan.FromMinutes(10))
                {
                    using var prune = connection.CreateCommand();
                    prune.Transaction = transaction;
                    prune.CommandText = "DELETE FROM tracks WHERE at < $cutoff";
                    prune.Parameters.AddWithValue("$cutoff", (now - LiveTrackKinds.Retention).ToUnixTimeSeconds());
                    prune.ExecuteNonQuery();
                    _lastPrune = now;
                    foreach (var stale in _lastSample.Where(pair => now - pair.Value > TimeSpan.FromHours(1)).Select(pair => pair.Key).ToList())
                        _lastSample.TryRemove(stale, out _);
                }
                transaction.Commit();
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Live-layer history: couldn't record {Kind}", kind);
        }
    }

    public IReadOnlyList<LiveTrack> At(BoundingBox bounds, DateTimeOffset at, int maxPerKind = LiveTrackKinds.MaxPerKind)
    {
        var tracks = new List<LiveTrack>();
        try
        {
            using var connection = Open();
            foreach (var kind in LiveTrackKinds.All)
            {
                using var command = connection.CreateCommand();
                // SQLite returns the other columns from the row holding MAX(at) for each id.
                command.CommandText = """
                    SELECT id, title, lat, lon, heading, MAX(at) FROM tracks
                    WHERE kind = $kind AND at BETWEEN $from AND $to AND lat BETWEEN $south AND $north
                    GROUP BY id LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$from", (at - LiveTrackKinds.Tolerance(kind)).ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$to", at.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$south", bounds.South);
                command.Parameters.AddWithValue("$north", bounds.North);
                command.Parameters.AddWithValue("$limit", maxPerKind * 4);
                using var reader = command.ExecuteReader();
                var count = 0;
                while (reader.Read() && count < maxPerKind)
                {
                    var lat = reader.GetDouble(2);
                    var lon = reader.GetDouble(3);
                    if (!bounds.Contains(lat, lon)) continue;
                    tracks.Add(new LiveTrack(kind, reader.GetString(0), reader.GetString(1), lat, lon,
                        reader.IsDBNull(4) ? null : reader.GetDouble(4), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5))));
                    count++;
                }
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Live-layer history: couldn't read {At}", at.ToString("O", CultureInfo.InvariantCulture));
        }
        return tracks;
    }
}
