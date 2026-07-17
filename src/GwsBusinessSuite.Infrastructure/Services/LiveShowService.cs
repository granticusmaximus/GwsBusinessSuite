using System.Collections.Concurrent;
using System.Security.Cryptography;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.LiveShow;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// One recording file per session at {recordingsRootPath}/{sessionId:N}.webm - a single
// MediaRecorder instance's sequential chunks reconstruct a valid playable file when
// simply concatenated in arrival order, so AppendRecordingChunkAsync just appends bytes;
// no container-format reassembly is needed. WriteLocks serializes writes per session in
// case chunk uploads ever overlap (the browser is expected to await each one already).
public sealed class LiveShowService(
    IAppDbContext dbContext,
    string recordingsRootPath,
    ICurrentUserAccessor? currentUserAccessor = null) : ILiveShowService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> WriteLocks = new();
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(6);

    private readonly ICurrentUserAccessor _currentUserAccessor = currentUserAccessor ?? FixedCurrentUserAccessor.Unknown;

    public async Task<LiveShowSessionView?> GetActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = await dbContext.LiveShowSessions
            .FirstOrDefaultAsync(s => s.Status == LiveShowSessionStatuses.Live, cancellationToken);
        return session is null ? null : ToView(session);
    }

    public async Task<LiveShowSessionView> StartSessionAsync(string title, CancellationToken cancellationToken = default)
    {
        var stillLive = await dbContext.LiveShowSessions
            .Where(s => s.Status == LiveShowSessionStatuses.Live)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var previous in stillLive)
        {
            previous.Status = LiveShowSessionStatuses.Ended;
            previous.EndedAt = now;
        }

        var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        var session = new LiveShowSession
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Live Show" : title.Trim(),
            InviteToken = RandomNumberGenerator.GetHexString(32),
            InviteExpiresAt = now.Add(InviteLifetime),
            CreatedBy = username
        };
        dbContext.LiveShowSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToView(session);
    }

    public async Task EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await dbContext.LiveShowSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null || session.Status != LiveShowSessionStatuses.Live)
        {
            return;
        }

        session.Status = LiveShowSessionStatuses.Ended;
        session.EndedAt = DateTimeOffset.UtcNow;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        session.UpdatedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<LiveShowSessionView?> ValidateInviteTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        // Equality-only filter server-side; the expiry check is a DateTimeOffset range
        // comparison, which SQLite/EF Core can't translate (see feedback_sqlite_datetimeoffset)
        // so it happens client-side after materializing the (at most one) matching row.
        var candidates = await dbContext.LiveShowSessions
            .Where(s => s.InviteToken == token && s.Status == LiveShowSessionStatuses.Live)
            .ToListAsync(cancellationToken);
        var match = candidates.FirstOrDefault();

        return match is null || match.InviteExpiresAt <= DateTimeOffset.UtcNow ? null : ToView(match);
    }

    public async Task<IReadOnlyList<LiveShowRecordingView>> ListRecordingsAsync(CancellationToken cancellationToken = default)
    {
        var recordings = await dbContext.LiveShowRecordings.ToListAsync(cancellationToken);
        if (recordings.Count == 0)
        {
            return [];
        }

        var sessionIds = recordings.Select(r => r.SessionId).Distinct().ToList();
        var sessionTitles = await dbContext.LiveShowSessions
            .Where(s => sessionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Title, cancellationToken);

        return recordings
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new LiveShowRecordingView(
                r.Id,
                r.SessionId,
                sessionTitles.GetValueOrDefault(r.SessionId, "(deleted session)"),
                r.FileName,
                r.DurationSeconds,
                r.FileSizeBytes,
                r.CreatedAt))
            .ToList();
    }

    public async Task AppendRecordingChunkAsync(Guid sessionId, Stream chunkStream, CancellationToken cancellationToken = default)
    {
        var writeLock = WriteLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(recordingsRootPath);
            var filePath = GetFilePath(sessionId);
            await using var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.None);
            await chunkStream.CopyToAsync(fileStream, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task FinalizeRecordingAsync(Guid sessionId, int durationSeconds, CancellationToken cancellationToken = default)
    {
        // Idempotent by design - the normal shutdown path (LiveShow.razor's StopAsync)
        // calls this via the browser before tearing down the SignalR connection, but
        // LiveShowHub.OnDisconnectedAsync also calls it (through HandleBroadcasterDisconnectedAsync)
        // as a safety net for abnormal disconnects (crash, force-quit). Those two paths can
        // race - the hub's disconnect handler can fire before the deliberate shutdown's
        // EndSessionAsync call lands - so finalizing twice for the same session must be a
        // safe no-op rather than creating a duplicate LiveShowRecording row.
        // No more chunks should be appended for this session once finalization is being
        // attempted, regardless of outcome - remove the write lock on every exit path
        // (not just the happy path) so a session whose file went missing, or whose
        // finalize call otherwise short-circuits, doesn't leak its SemaphoreSlim for the
        // rest of the process lifetime.
        try
        {
            var alreadyFinalized = await dbContext.LiveShowRecordings.AnyAsync(r => r.SessionId == sessionId, cancellationToken);
            if (alreadyFinalized)
            {
                return;
            }

            var filePath = GetFilePath(sessionId);
            if (!File.Exists(filePath))
            {
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
            dbContext.LiveShowRecordings.Add(new LiveShowRecording
            {
                SessionId = sessionId,
                FileName = Path.GetFileName(filePath),
                DurationSeconds = durationSeconds,
                FileSizeBytes = fileInfo.Length,
                CreatedBy = username
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            WriteLocks.TryRemove(sessionId, out _);
        }
    }

    // Safety net for the broadcaster's browser disappearing without a clean StopAsync
    // (crash, force-quit, lid closed) - LiveShowHub.OnDisconnectedAsync calls this so the
    // session doesn't stay "Live" (and its invite link "valid") for up to its full 6-hour
    // lifetime, and so any partially-written recording still gets a discoverable DB row
    // instead of becoming an orphaned file nobody can find. Duration is computed
    // server-side from the session's own StartedAt rather than trusting a client-reported
    // value that was never sent.
    public async Task HandleBroadcasterDisconnectedAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await dbContext.LiveShowSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        var durationSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - session.StartedAt).TotalSeconds);
        await FinalizeRecordingAsync(sessionId, durationSeconds, cancellationToken);
        await EndSessionAsync(sessionId, cancellationToken);
    }

    public async Task<string?> GetRecordingFilePathAsync(Guid recordingId, CancellationToken cancellationToken = default)
    {
        var recording = await dbContext.LiveShowRecordings.FirstOrDefaultAsync(r => r.Id == recordingId, cancellationToken);
        if (recording is null)
        {
            return null;
        }

        var filePath = Path.Combine(recordingsRootPath, recording.FileName);
        return File.Exists(filePath) ? filePath : null;
    }

    private string GetFilePath(Guid sessionId) => Path.Combine(recordingsRootPath, $"{sessionId:N}.webm");

    private static LiveShowSessionView ToView(LiveShowSession session) => new(
        session.Id,
        session.Title,
        session.Status,
        session.StartedAt,
        session.EndedAt,
        session.InviteToken,
        session.InviteExpiresAt);
}
