namespace GwsBusinessSuite.Application.CameraIntel;

public enum CameraViewingMode
{
    LiveVideo,
    StillImage
}

public enum CameraAvailabilityState
{
    // The last health check saw the image change (or the source said it was just updated).
    AvailableNow,
    // Not checked yet, or a live video stream (those aren't health-checked).
    Unconfirmed,
    // Frozen, stale or offline at the last check.
    Unavailable
}

// What a camera can show and whether it's working, from its stream kind and the health checks
// CameraHealthService already makes (19-11-26, camera availability plan phases 3-4). Nothing here
// fetches anything or changes a stream URL.
public sealed record CameraAvailability(
    CameraViewingMode Mode,
    CameraAvailabilityState State,
    string Reason,
    DateTimeOffset? LastConfirmed)
{
    public string ModeLabel => Mode == CameraViewingMode.LiveVideo ? "Live video" : "Still image";

    public string StateLabel => State switch
    {
        CameraAvailabilityState.AvailableNow => "Available now",
        CameraAvailabilityState.Unavailable => "Not working",
        _ => "Not confirmed",
    };

    // Incident Capture stores frames, so only still images are recorded.
    public bool CanCapture => Mode == CameraViewingMode.StillImage && State != CameraAvailabilityState.Unavailable;

    public static CameraAvailability For(CameraFeed camera, CameraHealthReport? health)
    {
        var mode = camera.StreamKind == CameraStreamKind.Hls ? CameraViewingMode.LiveVideo : CameraViewingMode.StillImage;
        if (mode == CameraViewingMode.LiveVideo)
            return new(mode, CameraAvailabilityState.Unconfirmed, "Live video streams aren't checked ahead of time.", null);
        return health?.Status switch
        {
            CameraHealthStatus.Live => new(mode, CameraAvailabilityState.AvailableNow, health.Detail, health.CheckedAt),
            CameraHealthStatus.Frozen or CameraHealthStatus.Stale or CameraHealthStatus.Offline =>
                new(mode, CameraAvailabilityState.Unavailable, health.Detail, null),
            _ => new(mode, CameraAvailabilityState.Unconfirmed, health?.Detail ?? "Not checked yet.", null),
        };
    }

    // The Overwatch "cameras in view" list (19-11-26): working first (as Rank), nearest to the view's
    // middle, or most recently seen working.
    public const string SortWorking = "working";
    public const string SortNearest = "nearest";
    public const string SortRecent = "recent";

    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, string sort, Func<T, CameraAvailability> availability, Func<T, double> miles, int take) =>
        (sort switch
        {
            SortNearest => items.OrderBy(miles),
            SortRecent => items.OrderByDescending(item => availability(item).LastConfirmed ?? DateTimeOffset.MinValue).ThenBy(miles),
            _ => items.OrderBy(item => (int)availability(item).State).ThenBy(miles),
        }).Take(take).ToList();

    // Working cameras first, then unchecked ones, then broken ones; nearest first within each.
    public static IReadOnlyList<T> Rank<T>(IEnumerable<T> items, Func<T, CameraAvailability> availability, Func<T, double> miles, int take) =>
        Sort(items, SortWorking, availability, miles, take);
}
