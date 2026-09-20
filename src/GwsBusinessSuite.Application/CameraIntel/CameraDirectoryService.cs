using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Fans out to every registered ICameraFeedProvider in parallel and merges the results for the
// globe's current view. Deliberately thin - each provider already owns its own caching/failure
// handling (see WsdotTrafficCameraProvider/WindyWebcamProvider), so this only adds the one thing
// that's genuinely cross-cutting: isolating one misbehaving provider (including a future
// third-party one that doesn't follow the "never throw" convention the two built-in providers
// use) from taking down every other source's cameras.
public sealed class CameraDirectoryService(
    IEnumerable<ICameraFeedProvider> providers,
    ILogger<CameraDirectoryService> logger)
{
    public async Task<IReadOnlyList<CameraFeed>> GetCamerasInBoundingBoxAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var tasks = providers.Select(provider => FetchSafelyAsync(provider, bbox, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(cameras => cameras).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchSafelyAsync(ICameraFeedProvider provider, BoundingBox bbox, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetCamerasAsync(bbox, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Camera provider {SourceName} failed; continuing with the other sources.", provider.SourceName);
            return [];
        }
    }
}
