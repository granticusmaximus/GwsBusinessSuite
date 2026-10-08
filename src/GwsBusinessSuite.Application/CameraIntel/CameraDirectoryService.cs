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
    // Pins pushed to the globe for one view. A world-wide view would otherwise send every camera
    // from every source over the circuit and into Cesium; past this many, the view is thinned.
    public const int DefaultMaxPins = 1500;

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasInBoundingBoxAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        // Providers that declare a coverage area are only asked about views that overlap it.
        var tasks = providers
            .Where(provider => provider.Coverage is not { } coverage || coverage.Intersects(bbox))
            .Select(provider => FetchSafelyAsync(provider, bbox, cancellationToken));
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

    // Every camera in the view, thinned to at most maxPins when the view is wide (zoomed out).
    public async Task<CameraViewResult> GetCamerasForViewAsync(BoundingBox bbox, int maxPins = DefaultMaxPins,
        Func<CameraFeed, bool>? include = null, CancellationToken cancellationToken = default)
    {
        var all = await GetCamerasInBoundingBoxAsync(bbox, cancellationToken);
        var matching = include is null ? all : all.Where(include).ToList();
        return new CameraViewResult(Thin(matching, bbox, maxPins), matching.Count);
    }

    // Keeps the view's geographic spread rather than the first N cameras (which would all come
    // from whichever source happened to answer first): the view is split into a grid of about
    // maxPins cells and the first camera (by id, so it's stable between pans) in each cell kept.
    public static IReadOnlyList<CameraFeed> Thin(IReadOnlyList<CameraFeed> cameras, BoundingBox bbox, int maxPins)
    {
        if (maxPins <= 0) return [];
        if (cameras.Count <= maxPins) return cameras;

        var side = Math.Max(1, (int)Math.Floor(Math.Sqrt(maxPins)));
        var latSpan = Math.Max(1e-9, bbox.North - bbox.South);
        var lonSpan = Math.Max(1e-9, bbox.East - bbox.West);
        return cameras
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .GroupBy(c => (
                Row: Math.Clamp((int)((c.Latitude - bbox.South) / latSpan * side), 0, side - 1),
                Col: Math.Clamp((int)((c.Longitude - bbox.West) / lonSpan * side), 0, side - 1)))
            .Select(g => g.First())
            .Take(maxPins)
            .ToList();
    }
}

public sealed record CameraViewResult(IReadOnlyList<CameraFeed> Cameras, int TotalInView)
{
    public bool IsThinned => Cameras.Count < TotalInView;
}
