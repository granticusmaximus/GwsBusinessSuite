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
    ILogger<CameraDirectoryService> logger,
    SourceStatusTracker? statusTracker = null)
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
            var cameras = await provider.GetCamerasAsync(bbox, cancellationToken);
            statusTracker?.RecordSuccess(provider.SourceName, SourceStatusTracker.CameraKind, cameras.Count);
            return cameras;
        }
        catch (Exception ex)
        {
            statusTracker?.RecordFailure(provider.SourceName, SourceStatusTracker.CameraKind, ex.GetType().Name);
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
    // from whichever source happened to answer first). Cameras cluster heavily (a world view is
    // mostly the U.S. and Europe), so a single fixed grid leaves most cells empty - found live: a
    // 38x38 world grid kept 54 of 28,035 cameras. Coarse to fine instead: one camera per cell of
    // a coarse grid first (so an isolated camera is never dropped), then the grid is refined and
    // cells not yet represented add one camera each (evenly spaced when there are more cells
    // than room left), and finally any remaining room is filled round-robin. Ordering by id keeps
    // the choice stable between pans.
    public static IReadOnlyList<CameraFeed> Thin(IReadOnlyList<CameraFeed> cameras, BoundingBox bbox, int maxPins)
    {
        if (maxPins <= 0) return [];
        if (cameras.Count <= maxPins) return cameras;

        var ordered = cameras.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        var latSpan = Math.Max(1e-9, bbox.North - bbox.South);
        var lonSpan = Math.Max(1e-9, bbox.East - bbox.West);
        var picked = new List<CameraFeed>(maxPins);
        var pickedIds = new HashSet<string>(StringComparer.Ordinal);

        void Pick(CameraFeed camera)
        {
            if (picked.Count < maxPins && pickedIds.Add(camera.Id)) picked.Add(camera);
        }

        for (var side = Math.Max(1, (int)Math.Floor(Math.Sqrt(maxPins))); side <= 8192 && picked.Count < maxPins; side *= 2)
        {
            var newCells = ordered
                .GroupBy(c => (
                    Row: Math.Clamp((int)((c.Latitude - bbox.South) / latSpan * side), 0, side - 1),
                    Col: Math.Clamp((int)((c.Longitude - bbox.West) / lonSpan * side), 0, side - 1)))
                .Where(g => !g.Any(c => pickedIds.Contains(c.Id)))
                .OrderBy(g => g.Key.Row).ThenBy(g => g.Key.Col)
                .Select(g => g.First())
                .ToList();
            var room = maxPins - picked.Count;
            if (newCells.Count <= room)
            {
                newCells.ForEach(Pick);
            }
            else
            {
                var step = newCells.Count / (double)room;
                for (var i = 0; i < room; i++) Pick(newCells[(int)(i * step)]);
            }
        }

        foreach (var camera in ordered)
        {
            if (picked.Count >= maxPins) break;
            Pick(camera);
        }
        return picked;
    }
}

public sealed record CameraViewResult(IReadOnlyList<CameraFeed> Cameras, int TotalInView)
{
    public bool IsThinned => Cameras.Count < TotalInView;
}
