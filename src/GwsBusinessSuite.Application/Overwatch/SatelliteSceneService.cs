using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Overwatch;

// Satellite scenes on click (Overwatch, 13-11-26): the recent clear-sky Sentinel-2 passes over a
// point (10 m true colour, about every 5 days), from Microsoft's Planetary Computer: its STAC search
// finds the scenes and its tile service draws one as map tiles. Free, no key, browser-readable (CORS).
public sealed record SatelliteScene(string Id, DateTimeOffset At, double CloudPercent, string TileUrlTemplate);

public sealed class SatelliteSceneService(HttpClient httpClient, ILogger<SatelliteSceneService> logger)
{
    private const string Search = "https://planetarycomputer.microsoft.com/api/stac/v1/search";
    private const string Tiles = "https://planetarycomputer.microsoft.com/api/data/v1/item/tiles/WebMercatorQuad/{z}/{x}/{y}@1x";
    public const int MaxScenes = 8;
    public const double MaxCloudPercent = 25;
    public const int LookBackDays = 90;

    public async Task<IReadOnlyList<SatelliteScene>> NearAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-LookBackDays).ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);
        var body = new
        {
            collections = new[] { "sentinel-2-l2a" },
            intersects = new { type = "Point", coordinates = new[] { longitude, latitude } },
            datetime = since + "/..",
            limit = MaxScenes * 2,
            sortby = new[] { new { field = "properties.datetime", direction = "desc" } },
            query = new Dictionary<string, object> { ["eo:cloud_cover"] = new { lt = MaxCloudPercent } },
        };
        try
        {
            using var response = await httpClient.PostAsJsonAsync(Search, body, ct);
            response.EnsureSuccessStatusCode();
            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Satellite scenes (Planetary Computer) couldn't be read");
            return [];
        }
    }

    // One scene per day (a pass can be split into several tiles; the clearest is kept).
    public static IReadOnlyList<SatelliteScene> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) return [];
        var scenes = new List<SatelliteScene>();
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("id", out var idElement) || idElement.GetString() is not { Length: > 0 } id
                || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) continue;
            if (!feature.TryGetProperty("properties", out var properties)
                || !properties.TryGetProperty("datetime", out var dt) || !DateTimeOffset.TryParse(dt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) continue;
            var cloud = properties.TryGetProperty("eo:cloud_cover", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 100;
            scenes.Add(new SatelliteScene(id, at, Math.Round(cloud, 1), TileTemplate(id)));
        }
        return scenes.GroupBy(scene => scene.At.UtcDateTime.Date)
            .Select(day => day.OrderBy(scene => scene.CloudPercent).First())
            .OrderByDescending(scene => scene.At)
            .Take(MaxScenes)
            .ToList();
    }

    public static string TileTemplate(string itemId) =>
        $"{Tiles}?collection=sentinel-2-l2a&item={Uri.EscapeDataString(itemId)}&assets=visual&asset_bidx=visual%7C1%2C2%2C3&nodata=0&format=png";
}
