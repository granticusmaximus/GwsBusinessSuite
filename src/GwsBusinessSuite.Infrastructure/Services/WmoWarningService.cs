using System.Globalization;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Hazards;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Severe-weather warnings worldwide (Overwatch, 19-11-26) from the WMO Severe Weather Information
// Centre's public index of current CAP alerts (key-less, about 2,000 alerts from ~40 national
// weather services). The index has no coordinates and the site refuses automated requests for the
// alert files themselves (403), so nothing else is fetched: warnings are placed by their issuing
// country (the alert path starts with its ISO code, "ar-smn-es/..."), one pin per country at its
// worst current warning, with the warnings listed in its panel. The US is left out: Overwatch's NWS
// layer already draws its warnings with their real areas.
public sealed class WmoWarningService(HttpClient httpClient, IMemoryCache cache, ILogger<WmoWarningService> logger)
{
    public const string IndexUrl = "https://severeweather.wmo.int/v2/json/wmo_all.json";
    public const string SiteUrl = "https://severeweather.wmo.int/v2/";
    private const string CacheKey = "sky:wmo-warnings";
    private const int ListedPerCountry = 8;
    private static readonly HashSet<string> CoveredElsewhere = new(StringComparer.OrdinalIgnoreCase) { "us" };

    public async Task<IReadOnlyList<HazardFeature>> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<HazardFeature>? cached) && cached is not null) return cached;
        try
        {
            var warnings = Parse(await httpClient.GetStringAsync(IndexUrl, ct), DateTimeOffset.UtcNow, WorldNewsPlaces.Instance);
            cache.Set(CacheKey, warnings, TimeSpan.FromMinutes(15));
            return warnings;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "WMO severe-weather layer: the alert index couldn't be read");
            return [];
        }
    }

    public static IReadOnlyList<HazardFeature> Parse(string json, DateTimeOffset now, WorldNewsPlaces places)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];

        var warnings = new List<(string Code, int Severity, string Event, string Area, DateTimeOffset? Expires)>();
        foreach (var item in items.EnumerateArray())
        {
            var path = Text(item, "capURL") ?? Text(item, "url");
            var code = path?.Split('-', 2)[0];
            if (code is not { Length: 2 } || CoveredElsewhere.Contains(code)) continue;
            var expires = Time(Text(item, "expires"));
            if (expires is { } until && until < now) continue;
            var severity = item.TryGetProperty("s", out var s) && s.TryGetInt32(out var level) ? level : 0;
            var name = Text(item, "event") is { Length: > 0 } e && !e.Contains('=') ? e : Text(item, "headline") ?? "Weather warning";
            warnings.Add((code, severity, Clean(name, 80), Clean(Text(item, "areaDesc") ?? string.Empty, 90), expires));
        }

        return warnings
            .GroupBy(w => w.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Country: places.Country(group.Key), Warnings: group.OrderByDescending(w => w.Severity).ThenBy(w => w.Expires ?? DateTimeOffset.MaxValue).ToList()))
            .Where(c => c.Country is not null)
            .Select(c =>
            {
                var worst = c.Warnings[0].Severity;
                var serious = c.Warnings.Count(w => w.Severity >= 3);
                var lines = c.Warnings.Take(ListedPerCountry).Select(w =>
                    $"{SeverityName(w.Severity)}: {w.Event}" + (w.Area.Length > 0 ? $" · {w.Area}" : "")
                    + (w.Expires is { } until ? $" · until {until.ToLocalGwsDateTime()}" : ""));
                var more = c.Warnings.Count > ListedPerCountry ? $"\n…and {c.Warnings.Count - ListedPerCountry} more." : "";
                return new HazardFeature(
                    $"wmo-{c.Country!.Code.ToLowerInvariant()}", "weather-warning",
                    $"{c.Country.Name}: {c.Warnings.Count} weather {(c.Warnings.Count == 1 ? "warning" : "warnings")}"
                        + (serious > 0 ? $" ({serious} severe or extreme)" : ""),
                    string.Join("\n", lines) + more + "\nPlaced at the country's centre: the warnings cover the areas named, not this point.",
                    c.Country.Latitude, c.Country.Longitude,
                    worst >= 3 ? "major" : worst == 2 ? "moderate" : "minor",
                    SiteUrl, null);
            })
            .OrderByDescending(f => f.Severity == "major").ThenBy(f => f.Title, StringComparer.Ordinal)
            .ToList();
    }

    private static string SeverityName(int level) => level switch
    {
        4 => "Extreme",
        3 => "Severe",
        2 => "Moderate",
        1 => "Minor",
        _ => "Unknown",
    };

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;

    // The index's times have no zone; they match the alerts' own UTC times.
    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    private static string Clean(string text, int max)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }
}
