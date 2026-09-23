namespace GwsBusinessSuite.Application.CameraIntel;

// On-demand, single-frame vision analysis for one Overwatch camera (the stream panel's ANALYZE
// button). Deliberately synchronous/single-shot and single-camera - no polling, no cross-camera
// automation. That's an explicit, deferred phase 2 once this pipeline's real latency/cost is
// measured against a live vision model.
public interface ICameraSnapshotAnalysisService
{
    Task<CameraSnapshotAnalysisResult> AnalyzeAsync(CameraFeed camera, CancellationToken cancellationToken = default);
}

// Message carries either the model's plain-English description (Succeeded = true) or a clear,
// user-facing failure reason (Succeeded = false) - "not configured", "not supported for live
// video feeds", "snapshot unavailable", "analysis timed out", or a generic failure. The Razor
// layer never needs to catch an exception from this service - AnalyzeAsync never throws out.
public sealed record CameraSnapshotAnalysisResult(bool Succeeded, string Message);
