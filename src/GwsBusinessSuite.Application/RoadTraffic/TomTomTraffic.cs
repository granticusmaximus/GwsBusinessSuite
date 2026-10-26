using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.RoadTraffic;

// Overwatch:TomTomApiKey is the secret (OVERWATCH_TOMTOM_API_KEY in the droplet's .env).
// Overwatch:TomTomReferer: the key is locked to the domain it was registered for in TomTom's
// dashboard (grantwatson.dev, checked 2026-10-26), and every call comes from this server, which
// has no page of its own to refer from - so it names that domain. The daily limits keep a busy
// session inside TomTom's free allowance (tiles and other calls are counted separately).
public sealed record TomTomTrafficOptions(string? ApiKey, string? Referer, int DailyTileLimit = 40000, int DailyLookupLimit = 2000);

// TomTom's live speed for the stretch of road nearest a point, against its free-flow speed.
public sealed record LiveRoadFlow(double CurrentMph, double FreeFlowMph, int? CurrentTravelSeconds, int? FreeFlowTravelSeconds,
    double? Confidence, bool Closed)
{
    public int PercentOfFreeFlow => FreeFlowMph > 0 ? (int)Math.Round(100 * CurrentMph / FreeFlowMph) : 100;
}

// Live congestion for Overwatch from TomTom's Traffic API, worldwide: flow tiles for the map and
// the live speed at a point. The key never leaves the server: the globe asks this app for tiles.
public sealed class TomTomTrafficService(HttpClient httpClient, IMemoryCache cache, TomTomTrafficOptions options, TimeProvider time,
    ILogger<TomTomTrafficService> logger)
{
    private const string Root = "https://api.tomtom.com/traffic/";
    // Green to red against each road's own free-flow speed; brighter than relative0-dark on the
    // globe's dark imagery.
    public const string FlowStyle = "relative0";
    public const string AttributionUrl = "https://www.tomtom.com/products/traffic-apis/";
    private static readonly TimeSpan TileCacheFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LookupCacheFor = TimeSpan.FromMinutes(2);

    // Served in place of a tile TomTom can't give, so the map just shows nothing there.
    public static readonly byte[] EmptyTile = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAXpeqz8AAAAASUVORK5CYII=");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ApiKey);

    public async Task<byte[]?> GetFlowTileAsync(int zoom, int x, int y, CancellationToken ct = default)
    {
        if (!IsConfigured || zoom is < 0 or > 22) return null;
        var size = 1L << zoom;
        if (x < 0 || y < 0 || x >= size || y >= size) return null;
        var key = string.Create(CultureInfo.InvariantCulture, $"tomtom:tile:{zoom}:{x}:{y}");
        if (cache.TryGetValue(key, out byte[]? cached) && cached is not null) return cached;
        if (!TryCount("tile", options.DailyTileLimit)) return null;
        try
        {
            using var request = Request(string.Create(CultureInfo.InvariantCulture, $"{Root}map/4/tile/flow/{FlowStyle}/{zoom}/{x}/{y}.png?tileSize=256"));
            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TomTom flow tile answered {Status}", (int)response.StatusCode);
                return null;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes is not [0x89, 0x50, 0x4E, 0x47, ..]) return null;
            cache.Set(key, bytes, TileCacheFor);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TomTom flow tile failed");
            return null;
        }
    }

    // Null when there's no road with live data near the point (remembered for the cache time too).
    public async Task<LiveRoadFlow?> GetFlowAtAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        if (!IsConfigured || latitude is < -85 or > 85 || longitude is < -180 or > 180) return null;
        var key = string.Create(CultureInfo.InvariantCulture, $"tomtom:flow:{latitude:0.0000}:{longitude:0.0000}");
        if (cache.TryGetValue(key, out LiveRoadFlow? cached)) return cached;
        if (!TryCount("lookup", options.DailyLookupLimit)) return null;
        try
        {
            using var request = Request(string.Create(CultureInfo.InvariantCulture,
                $"{Root}services/4/flowSegmentData/{FlowStyle}/10/json?point={latitude:0.######},{longitude:0.######}&unit=mph"));
            using var response = await httpClient.SendAsync(request, ct);
            // 400 is TomTom's "point too far from the nearest road segment".
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                cache.Set(key, (LiveRoadFlow?)null, LookupCacheFor);
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TomTom flow lookup answered {Status}", (int)response.StatusCode);
                return null;
            }
            var flow = ParseFlow(await response.Content.ReadAsStringAsync(ct));
            cache.Set(key, flow, LookupCacheFor);
            return flow;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TomTom flow lookup failed");
            return null;
        }
    }

    public static LiveRoadFlow? ParseFlow(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("flowSegmentData", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        double? Number(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        if (Number("currentSpeed") is not { } current || Number("freeFlowSpeed") is not { } freeFlow) return null;
        var closed = data.TryGetProperty("roadClosure", out var closure) && closure.ValueKind == JsonValueKind.True;
        return new LiveRoadFlow(current, freeFlow, (int?)Number("currentTravelTime"), (int?)Number("freeFlowTravelTime"), Number("confidence"), closed);
    }

    private HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{url}&key={Uri.EscapeDataString(options.ApiKey!.Trim())}");
        if (Uri.TryCreate(options.Referer, UriKind.Absolute, out var referer)) request.Headers.Referrer = referer;
        return request;
    }

    // Counted per UTC day and kind of call; past the limit, nothing more is asked of TomTom today.
    private bool TryCount(string kind, int limit)
    {
        var day = time.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var counter = cache.GetOrCreate($"tomtom:count:{kind}:{day}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(2);
            return new Counter();
        })!;
        var count = Interlocked.Increment(ref counter.Value);
        if (count == limit + 1) logger.LogWarning("TomTom {Kind} allowance for today ({Limit}) is used up", kind, limit);
        return count <= limit;
    }

    private sealed class Counter
    {
        public int Value;
    }
}
