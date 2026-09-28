using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// British Columbia's open-data highway camera CSV (data.gov.bc.ca) - genuinely free with no API
// key, confirmed directly. This is a plain CSV file, not JSON/ArcGIS like every other provider in
// this file, so it needs its own minimal (but real, quote-aware) row parser - some fields
// (`highway_locationDescription`, `caption`) contain embedded commas and are RFC4180-quoted,
// confirmed live, so a naive comma-split would corrupt those rows. `links_imageDisplay` is a
// direct, ready-to-use JPEG URL - no construction needed.
public sealed class DriveBcCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<DriveBcCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:drivebc:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "DriveBC";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            cache.Set(CacheKey, allCameras, CacheDuration);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            const string url = "dataset/6b39a910-6c77-476f-ac96-7b4f18849b1c/resource/" +
                "a9d52d85-8402-4ce7-b2ac-a2779837c48a/download/webcams.csv";
            var csv = await httpClient.GetStringAsync(url, cancellationToken);
            var rows = ParseCsv(csv);
            if (rows.Count < 2) return [];

            var header = rows[0];
            var idIdx = header.IndexOf("id");
            var imageIdx = header.IndexOf("links_imageDisplay");
            var camNameIdx = header.IndexOf("camName");
            var captionIdx = header.IndexOf("caption");
            var highwayIdx = header.IndexOf("highway_locationDescription");
            var latIdx = header.IndexOf("latitude");
            var lonIdx = header.IndexOf("longitude");
            if (idIdx < 0 || imageIdx < 0 || latIdx < 0 || lonIdx < 0) return [];

            var results = new List<CameraFeed>();
            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Count <= Math.Max(latIdx, lonIdx)) continue;

                var imageUrl = Field(row, imageIdx);
                if (string.IsNullOrWhiteSpace(imageUrl)) continue;
                if (!double.TryParse(Field(row, latIdx).Trim(), out var lat)) continue;
                if (!double.TryParse(Field(row, lonIdx).Trim(), out var lon)) continue;

                var name = Field(row, camNameIdx);
                if (string.IsNullOrWhiteSpace(name)) name = Field(row, highwayIdx);
                if (string.IsNullOrWhiteSpace(name)) name = Field(row, captionIdx);

                results.Add(new CameraFeed(
                    Id: $"drivebc-{Field(row, idIdx)}",
                    Name: string.IsNullOrWhiteSpace(name) ? $"DriveBC Camera {Field(row, idIdx)}" : $"DriveBC: {name}",
                    Latitude: lat,
                    Longitude: lon,
                    StreamUrl: imageUrl,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://drivebc.ca"));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException)
        {
            logger.LogWarning(ex, "Failed to fetch DriveBC traffic cameras.");
            return [];
        }
    }

    private static string Field(List<string> row, int index) => index >= 0 && index < row.Count ? row[index] : "";

    // A minimal RFC4180-style CSV parser: handles double-quoted fields (including embedded commas
    // and escaped "" quotes within them) - enough for this specific government open-data export,
    // not a general-purpose CSV library replacement.
    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;

        void EndField() { row.Add(field.ToString()); field.Clear(); }
        void EndRow() { EndField(); rows.Add(row); row = []; }

        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') EndField();
            else if (c == '\r') { }
            else if (c == '\n') EndRow();
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }
}
