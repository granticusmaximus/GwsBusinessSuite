using System.Globalization;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Hong Kong Transport Department's traffic snapshot cameras, from DATA.GOV.HK - key-less,
// verified live 2026-10-08 (~1,000 cameras). The location list is a UTF-16 tab-separated file
// (despite the .csv name) with WGS84 lat/lon and a direct JPEG per camera that TD refreshes in
// place every couple of minutes.
public sealed class HongKongTdCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<HongKongTdCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:hk-td:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    public string SourceName => "HK Transport Department";

    public BoundingBox? Coverage { get; } = new(22.6, 22.1, 114.5, 113.8);

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            if (allCameras.Count > 0) cache.Set(CacheKey, allCameras, CacheDuration);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await httpClient.GetByteArrayAsync("td/traffic-snapshot-images/code/Traffic_Camera_Locations_En.csv", cancellationToken);
            return Parse(Decode(bytes));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Hong Kong TD traffic cameras.");
            return [];
        }
    }

    // The live file starts with the UTF-16 LE byte-order mark twice (FF FE FF FE, seen
    // 2026-10-08), so every leading BOM is trimmed after decoding. UTF-8 is the fallback if TD
    // ever changes the encoding.
    public static string Decode(byte[] bytes)
    {
        var text = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes)
            : bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);
        return text.TrimStart((char)0xFEFF);
    }

    public static IReadOnlyList<CameraFeed> Parse(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return [];
        var header = lines[0].Split('\t').Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        int key = Col("key"), description = Col("description"), lat = Col("latitude"), lon = Col("longitude"), url = Col("url");
        if (new[] { key, lat, lon, url }.Any(i => i < 0)) return [];

        var cameras = new List<CameraFeed>();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split('\t');
            if (cells.Length <= new[] { key, lat, lon, url }.Max()) continue;
            if (!double.TryParse(cells[lat], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                || !double.TryParse(cells[lon], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)) continue;
            var imageUrl = cells[url].Trim();
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
            var name = description >= 0 && description < cells.Length ? cells[description].Trim() : cells[key].Trim();
            cameras.Add(new CameraFeed(
                Id: $"hk-td-{cells[key].Trim()}",
                Name: $"Hong Kong: {name}",
                Latitude: latitude,
                Longitude: longitude,
                StreamUrl: imageUrl,
                StreamKind: CameraStreamKind.Snapshot,
                SourceName: "HK Transport Department",
                SourceAttributionUrl: "https://data.gov.hk"));
        }
        return cameras.GroupBy(c => c.Id).Select(g => g.First()).ToList();
    }
}
