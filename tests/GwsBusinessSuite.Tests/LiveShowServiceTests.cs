using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.LiveShow;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Covers the DB/file-I/O side only (session lifecycle, invite validation, recording file
// assembly) - actual WebRTC signaling/peer connections require a real browser and aren't
// exercised here, same posture as SshTerminalServiceTests' pre-connection-only coverage.
public sealed class LiveShowServiceTests : IDisposable
{
    private readonly string _recordingsPath = Path.Combine(Path.GetTempPath(), "gws-liveshow-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartSessionAsync_ShouldCreateLiveSession_WithUniqueToken()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var session = await service.StartSessionAsync("My Show");

        session.Title.Should().Be("My Show");
        session.Status.Should().Be(LiveShowSessionStatuses.Live);
        session.InviteToken.Should().NotBeNullOrWhiteSpace();
        session.InviteExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task GetActiveSessionAsync_ShouldReturnNull_WhenNothingIsLive()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var result = await service.GetActiveSessionAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveSessionAsync_ShouldReturnTheLiveSession_WhenOneExists()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var started = await service.StartSessionAsync("My Show");

        var result = await service.GetActiveSessionAsync();

        result.Should().NotBeNull();
        result!.Id.Should().Be(started.Id);
    }

    [Fact]
    public async Task GetActiveSessionAsync_ShouldReturnNull_AfterTheSessionEnds()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var started = await service.StartSessionAsync("My Show");
        await service.EndSessionAsync(started.Id);

        var result = await service.GetActiveSessionAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task HandleBroadcasterDisconnectedAsync_ShouldEndSession_AndFinalizeAnyInProgressRecording()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var session = await service.StartSessionAsync("My Show");
        await service.AppendRecordingChunkAsync(session.Id, new MemoryStream(Encoding.UTF8.GetBytes("partial")));

        await service.HandleBroadcasterDisconnectedAsync(session.Id);

        (await service.GetActiveSessionAsync()).Should().BeNull();
        var recordings = await service.ListRecordingsAsync();
        recordings.Should().ContainSingle(r => r.SessionId == session.Id);
    }

    [Fact]
    public async Task HandleBroadcasterDisconnectedAsync_ShouldNoOp_WhenSessionDoesNotExist()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        // Should not throw even though nothing matches.
        await service.HandleBroadcasterDisconnectedAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task FinalizeRecordingAsync_ShouldBeIdempotent_WhenCalledTwiceForTheSameSession()
    {
        // Regression coverage: LiveShowHub.OnDisconnectedAsync's cleanup path and the
        // browser's normal StopAsync shutdown path can both call finalize for the same
        // session in a race - the second call must not create a duplicate recording row.
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var session = await service.StartSessionAsync("My Show");
        await service.AppendRecordingChunkAsync(session.Id, new MemoryStream(Encoding.UTF8.GetBytes("data")));

        await service.FinalizeRecordingAsync(session.Id, 10);
        await service.FinalizeRecordingAsync(session.Id, 20);

        var recordings = await service.ListRecordingsAsync();
        recordings.Should().ContainSingle(r => r.SessionId == session.Id);
    }

    [Fact]
    public async Task StartSessionAsync_ShouldEndAnyPreviousLiveSession()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var first = await service.StartSessionAsync("First Show");

        await service.StartSessionAsync("Second Show");

        var reloaded = await db.LiveShowSessions.FirstAsync(s => s.Id == first.Id);
        reloaded.Status.Should().Be(LiveShowSessionStatuses.Ended);
        reloaded.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task EndSessionAsync_ShouldSetEndedAt()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var session = await service.StartSessionAsync("My Show");

        await service.EndSessionAsync(session.Id);

        var reloaded = await db.LiveShowSessions.FirstAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(LiveShowSessionStatuses.Ended);
        reloaded.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateInviteTokenAsync_ShouldReturnSession_WhenLiveAndNotExpired()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var session = await service.StartSessionAsync("My Show");

        var result = await service.ValidateInviteTokenAsync(session.InviteToken);

        result.Should().NotBeNull();
        result!.Id.Should().Be(session.Id);
    }

    [Fact]
    public async Task ValidateInviteTokenAsync_ShouldReturnNull_WhenTokenUnknown()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var result = await service.ValidateInviteTokenAsync("not-a-real-token");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateInviteTokenAsync_ShouldReturnNull_WhenSessionEnded()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var session = await service.StartSessionAsync("My Show");
        await service.EndSessionAsync(session.Id);

        var result = await service.ValidateInviteTokenAsync(session.InviteToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateInviteTokenAsync_ShouldReturnNull_WhenExpired()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var session = await service.StartSessionAsync("My Show");

        var entity = await db.LiveShowSessions.FirstAsync(s => s.Id == session.Id);
        entity.InviteExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var result = await service.ValidateInviteTokenAsync(session.InviteToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task AppendAndFinalizeRecording_ShouldCreateRecordingRow_WithCorrectFileSize()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var session = await service.StartSessionAsync("My Show");

        var chunk1 = Encoding.UTF8.GetBytes("hello ");
        var chunk2 = Encoding.UTF8.GetBytes("world");
        await service.AppendRecordingChunkAsync(session.Id, new MemoryStream(chunk1));
        await service.AppendRecordingChunkAsync(session.Id, new MemoryStream(chunk2));
        await service.FinalizeRecordingAsync(session.Id, durationSeconds: 42);

        var recordings = await service.ListRecordingsAsync();
        recordings.Should().ContainSingle();
        var recording = recordings[0];
        recording.SessionId.Should().Be(session.Id);
        recording.SessionTitle.Should().Be("My Show");
        recording.DurationSeconds.Should().Be(42);
        recording.FileSizeBytes.Should().Be(chunk1.Length + chunk2.Length);

        var filePath = await service.GetRecordingFilePathAsync(recording.Id);
        filePath.Should().NotBeNull();
        (await File.ReadAllTextAsync(filePath!)).Should().Be("hello world");
    }

    [Fact]
    public async Task FinalizeRecordingAsync_ShouldNoOp_WhenNoChunksWereWritten()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var session = await service.StartSessionAsync("My Show");

        await service.FinalizeRecordingAsync(session.Id, durationSeconds: 10);

        (await service.ListRecordingsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecordingFilePathAsync_ShouldReturnNull_WhenRecordingNotFound()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var result = await service.GetRecordingFilePathAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ListRecordingsAsync_ShouldOrderNewestFirst()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var older = await service.StartSessionAsync("Older Show");
        await service.AppendRecordingChunkAsync(older.Id, new MemoryStream(Encoding.UTF8.GetBytes("a")));
        await service.FinalizeRecordingAsync(older.Id, 5);
        var olderRecording = (await db.LiveShowRecordings.FirstAsync(r => r.SessionId == older.Id));
        olderRecording.CreatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();

        var newer = await service.StartSessionAsync("Newer Show");
        await service.AppendRecordingChunkAsync(newer.Id, new MemoryStream(Encoding.UTF8.GetBytes("b")));
        await service.FinalizeRecordingAsync(newer.Id, 5);

        var recordings = await service.ListRecordingsAsync();
        recordings.Should().HaveCount(2);
        recordings[0].SessionTitle.Should().Be("Newer Show");
        recordings[1].SessionTitle.Should().Be("Older Show");
    }

    private LiveShowService CreateService(ApplicationDbContext db) => new(db, _recordingsPath);

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_recordingsPath))
            {
                Directory.Delete(_recordingsPath, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup of a temp test directory
        }
    }
}
