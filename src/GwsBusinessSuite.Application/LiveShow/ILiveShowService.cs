namespace GwsBusinessSuite.Application.LiveShow;

public interface ILiveShowService
{
    Task<LiveShowSessionView?> GetActiveSessionAsync(CancellationToken cancellationToken = default);
    Task<LiveShowSessionView> StartSessionAsync(string title, CancellationToken cancellationToken = default);
    Task EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    // Null if the token doesn't match a currently-Live session or has expired - callers
    // use this to gate the unauthenticated /watch/{token} viewer page.
    Task<LiveShowSessionView?> ValidateInviteTokenAsync(string token, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LiveShowRecordingView>> ListRecordingsAsync(CancellationToken cancellationToken = default);

    // Appends one MediaRecorder chunk to the session's on-disk recording file, in the
    // order it's called - see LiveShowRecording's doc comment for why raw concatenation
    // reconstructs a valid file. Chunks must be applied sequentially per session (the
    // caller endpoint is expected to await each upload before sending the next).
    Task AppendRecordingChunkAsync(Guid sessionId, Stream chunkStream, CancellationToken cancellationToken = default);

    Task FinalizeRecordingAsync(Guid sessionId, int durationSeconds, CancellationToken cancellationToken = default);

    Task<string?> GetRecordingFilePathAsync(Guid recordingId, CancellationToken cancellationToken = default);
}
