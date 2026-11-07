using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Hazards;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Humanitarian emergencies for Overwatch (07-11-26), key-less: the IFRC's GO emergencies from the
// last 60 days, each pinned at the centre of each country it names (up to three); pins that would
// sit on the same spot are set a little apart so each can be clicked. ReliefWeb's disasters RSS
// was planned too, but it answered this client 406 (Not Acceptable), a refusal, so it isn't used.
public sealed partial class HumanitarianService(HttpClient httpClient, IMemoryCache cache, TimeProvider time, ILogger<HumanitarianService> logger)
{
    private const string IfrcUrl = "https://goadmin.ifrc.org/api/v2/event/?limit=60&ordering=-disaster_start_date";
    private const string CacheKey = "overwatch:humanitarian";
    public static readonly TimeSpan IfrcWindow = TimeSpan.FromDays(60);
    private const int MaxCountries = 3;

    public async Task<IReadOnlyList<HazardFeature>> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<HazardFeature>? cached) && cached is not null) return cached;
        try
        {
            var features = SetApart(ParseIfrc(await httpClient.GetStringAsync(IfrcUrl, ct), WorldNewsPlaces.Instance, time.GetUtcNow() - IfrcWindow));
            cache.Set(CacheKey, features, TimeSpan.FromHours(1));
            return features;
        }
        catch (HostPausedException ex)
        {
            logger.LogDebug(ex, "Humanitarian layer: IFRC GO is paused after refusing us");
            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Humanitarian layer: IFRC GO failed");
            IReadOnlyList<HazardFeature> none = [];
            cache.Set(CacheKey, none, TimeSpan.FromMinutes(10));
            return none;
        }
    }

    // IFRC GO's emergencies started since `since`. Its severity level is 0 (yellow), 1 (orange) or
    // 2 (red): minor, moderate, major.
    public static IReadOnlyList<HazardFeature> ParseIfrc(string json, WorldNewsPlaces places, DateTimeOffset since)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        var features = new List<HazardFeature>();
        foreach (var e in results.EnumerateArray())
        {
            if (!DateTimeOffset.TryParse(Text(e, "disaster_start_date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var started)
                || started < since) continue;
            var id = e.TryGetProperty("id", out var idElement) ? idElement.ToString() : string.Empty;
            // "PRY: Civil Unrest - 11-2026 - …": the country's ISO code comes off the front.
            var name = Iso3Prefix().Replace(Text(e, "name") ?? "IFRC emergency", string.Empty);
            var title = Shorten(name, 110);
            var type = e.TryGetProperty("dtype", out var dtype) && dtype.ValueKind == JsonValueKind.Object ? Text(dtype, "name") : null;
            var affected = e.TryGetProperty("num_affected", out var count) && count.ValueKind == JsonValueKind.Number && count.GetInt64() > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{count.GetInt64():N0} people affected") : null;
            var level = e.TryGetProperty("ifrc_severity_level", out var severity) && severity.ValueKind == JsonValueKind.Number ? severity.GetInt32() : 0;
            var detail = string.Join(" · ", new[] { type, affected, "IFRC GO" }.Where(part => !string.IsNullOrWhiteSpace(part)));
            var countries = e.TryGetProperty("countries", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(c => places.Country(Text(c, "iso"))).OfType<WorldNewsCountry>().DistinctBy(c => c.Code).Take(MaxCountries).ToList()
                : [];
            features.AddRange(countries.Select(country => new HazardFeature($"ifrc-{id}-{country.Code}", HazardKinds.Humanitarian, title, detail,
                country.Latitude, country.Longitude, level switch { 2 => "major", 1 => "moderate", _ => "minor" },
                $"https://go.ifrc.org/emergencies/{Uri.EscapeDataString(id)}", started)));
        }
        return features;
    }

    // Pins on the same spot (one country, several emergencies) go round it in a small ring.
    public static IReadOnlyList<HazardFeature> SetApart(IReadOnlyList<HazardFeature> features) =>
        features.GroupBy(f => (Math.Round(f.Latitude, 2), Math.Round(f.Longitude, 2)))
            .SelectMany(spot => spot.Select((f, i) => i == 0 ? f : f with
            {
                Latitude = f.Latitude + 0.6 * Math.Sin(i * 2.4),
                Longitude = f.Longitude + 0.6 * Math.Cos(i * 2.4)
            }))
            .ToList();

    // Cut at a word near max, with an ellipsis.
    public static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max - 2);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd() + "…";
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"^[A-Z]{3}:\s*")]
    private static partial Regex Iso3Prefix();
}
