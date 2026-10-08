using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

public enum CameraHealthStatus
{
    // Not enough checks yet to say anything.
    Unknown,
    // The image changed between checks, or the source says it was updated recently.
    Live,
    // The same image came back on every check for at least FrozenAfter.
    Frozen,
    // The source's own Last-Modified header says the image is older than StaleAfter.
    Stale,
    // The last checks failed or returned something that isn't an image.
    Offline
}

public sealed record CameraHealthReport(string CameraId, CameraHealthStatus Status, string Detail, DateTimeOffset? CheckedAt);

// Snapshot cameras load straight into the browser from other sites, so the page can't read
// their pixels; health has to be judged server-side. Each check fetches the image, hashes it and
// keeps a short in-memory history per camera (process lifetime - a restart just starts the
// history over). Checks happen when someone opens a camera and, for favorites, every 10 minutes
// from CameraHealthBackgroundService, which is what gives "frozen" enough history to mean
// something. HLS cameras aren't checked (a playlist doesn't freeze the same way).
public sealed class CameraHealthService(
    HttpClient httpClient,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<CameraHealthService> logger)
{
    public static readonly TimeSpan FrozenAfter = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);
    private static readonly TimeSpan HistoryLifetime = TimeSpan.FromHours(6);
    private const int MaxSamples = 12;
    private const int MaxImageBytes = 5 * 1024 * 1024;
    // A second check within this window reuses the first, so several people opening the same
    // camera (or the panel's own re-checks) don't hammer a public DOT server.
    private static readonly TimeSpan MinCheckInterval = TimeSpan.FromSeconds(90);

    public sealed record Sample(DateTimeOffset At, bool Ok, string? Hash, DateTimeOffset? LastModified);

    private sealed class History
    {
        public readonly object Gate = new();
        public List<Sample> Samples { get; } = [];
    }

    private static string Key(string cameraId) => $"camera-health:{cameraId}";

    // The latest verdict without fetching anything - for filtering pins.
    public CameraHealthReport? GetKnown(string cameraId)
    {
        if (!cache.TryGetValue(Key(cameraId), out History? history) || history is null) return null;
        lock (history.Gate)
        {
            return Evaluate(cameraId, history.Samples, timeProvider.GetUtcNow());
        }
    }

    public async Task<CameraHealthReport> CheckAsync(CameraFeed camera, CancellationToken cancellationToken = default)
    {
        if (camera.StreamKind != CameraStreamKind.Snapshot)
            return new(camera.Id, CameraHealthStatus.Unknown, "Live video stream - not health-checked.", null);

        var now = timeProvider.GetUtcNow();
        var history = cache.GetOrCreate(Key(camera.Id), entry =>
        {
            entry.SlidingExpiration = HistoryLifetime;
            return new History();
        })!;

        lock (history.Gate)
        {
            if (history.Samples.Count > 0 && now - history.Samples[^1].At < MinCheckInterval)
                return Evaluate(camera.Id, history.Samples, now);
        }

        var sample = await FetchSampleAsync(camera.StreamUrl, now, cancellationToken);
        lock (history.Gate)
        {
            history.Samples.Add(sample);
            if (history.Samples.Count > MaxSamples) history.Samples.RemoveRange(0, history.Samples.Count - MaxSamples);
            return Evaluate(camera.Id, history.Samples, now);
        }
    }

    private async Task<Sample> FetchSampleAsync(string url, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            // Some sources (Singapore's) serve real JPEGs as application/octet-stream, so only an
            // explicit text/HTML answer - the classic "retired endpoint now serves its SPA" failure
            // - counts as not-an-image.
            if (!response.IsSuccessStatusCode || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > MaxImageBytes)
                return new(now, false, null, null);

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return new(now, false, null, null);
            return new(now, true, Convert.ToHexString(SHA256.HashData(bytes)), response.Content.Headers.LastModified);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Camera health check failed for {Url}", url);
            return new(now, false, null, null);
        }
    }

    public static CameraHealthReport Evaluate(string cameraId, IReadOnlyList<Sample> samples, DateTimeOffset now)
    {
        if (samples.Count == 0) return new(cameraId, CameraHealthStatus.Unknown, "Not checked yet.", null);
        var last = samples[^1];

        if (!last.Ok)
        {
            var failures = samples.Reverse().TakeWhile(s => !s.Ok).Count();
            var everOk = samples.Any(s => s.Ok);
            return failures >= 2 || !everOk
                ? new(cameraId, CameraHealthStatus.Offline, failures >= 2 ? $"The last {failures} checks failed." : "The source didn't return an image.", last.At)
                : new(cameraId, CameraHealthStatus.Unknown, "The last check failed; checking again.", last.At);
        }

        if (last.LastModified is { } modified && now - modified > StaleAfter)
            return new(cameraId, CameraHealthStatus.Stale, $"The source says this image was last updated {Describe(now - modified)} ago.", last.At);

        var ok = samples.Where(s => s.Ok).ToList();
        // Same picture since the first check that showed it: how long has it been unchanged?
        var unchangedSince = ok.Last().At;
        for (var i = ok.Count - 1; i >= 0 && ok[i].Hash == last.Hash; i--) unchangedSince = ok[i].At;
        var identicalChecks = ok.AsEnumerable().Reverse().TakeWhile(s => s.Hash == last.Hash).Count();

        if (identicalChecks >= 3 && last.At - unchangedSince >= FrozenAfter)
            return new(cameraId, CameraHealthStatus.Frozen, $"Same image on the last {identicalChecks} checks, over {Describe(last.At - unchangedSince)}.", last.At);

        if (identicalChecks < ok.Count || (last.LastModified is { } recent && now - recent <= FrozenAfter))
            return new(cameraId, CameraHealthStatus.Live, "The image is updating.", last.At);

        return new(cameraId, CameraHealthStatus.Unknown, "Checking whether the image updates.", last.At);
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 48 ? $"{(int)span.TotalDays} days"
        : span.TotalMinutes >= 90 ? $"{(int)span.TotalHours} hours"
        : $"{Math.Max(1, (int)span.TotalMinutes)} minutes";
}
