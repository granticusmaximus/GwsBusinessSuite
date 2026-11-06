using System.Globalization;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Hazards;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Internet outages by country for Overwatch (05-11-26, from the globalhackmap review): Georgia
// Tech's IODA, key-less. IODA watches each country through independent signals: routing (BGP),
// active probing of address blocks, a network telescope, and Google's traffic. Severity is how many
// of them saw the drop at once. Google traffic on its own is often noise (on 05-11-26 it alone had
// Finland "out" for six days), so that alone is only minor. Each outage is pinned at the country's
// centre, because IODA's events here are for the whole country.
public sealed class InternetOutageService(HttpClient httpClient, IMemoryCache cache, TimeProvider time, ILogger<InternetOutageService> logger)
{
    private const string EventsUrl = "https://api.ioda.inetintel.cc.gatech.edu/v2/outages/events";
    private const string CacheKey = "hazards:ioda";
    public const string GoogleTraffic = "gtr";
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<HazardFeature>> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<HazardFeature>? cached) && cached is not null) return cached;
        var until = time.GetUtcNow();
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{EventsUrl}?from={(until - Window).ToUnixTimeSeconds()}&until={until.ToUnixTimeSeconds()}&entityType=country&limit=500&format=codf");
        try
        {
            var outages = Parse(await httpClient.GetStringAsync(url, ct), WorldNewsPlaces.Instance);
            cache.Set(CacheKey, outages, TimeSpan.FromMinutes(10));
            return outages;
        }
        catch (HostPausedException ex)
        {
            logger.LogDebug(ex, "IODA is paused after refusing us");
            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Internet outage layer: IODA request failed");
            return [];
        }
    }

    public static IReadOnlyList<HazardFeature> Parse(string json, WorldNewsPlaces places)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var events = new List<(string Code, string Source, long Start, long End)>();
        foreach (var item in data.EnumerateArray())
        {
            var location = Text(item, "location");
            var source = Text(item, "datasource");
            if (location is null || !location.StartsWith("country/", StringComparison.Ordinal) || source is null
                || Number(item, "start") is not { } start) continue;
            var code = location["country/".Length..].ToUpperInvariant();
            events.Add((code, source, (long)start, (long)start + (long)(Number(item, "duration") ?? 0)));
        }

        var outages = new List<HazardFeature>();
        foreach (var country in events.GroupBy(e => e.Code).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (places.Country(country.Key) is not { } place) continue;
            var sources = country.Select(e => e.Source).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(SourceOrder).ToList();
            var measured = sources.Count(source => !source.Equals(GoogleTraffic, StringComparison.OrdinalIgnoreCase));
            var severity = measured >= 2 || sources.Count >= 3 ? "major" : measured == 1 ? "moderate" : "minor";
            var began = DateTimeOffset.FromUnixTimeSeconds(country.Min(e => e.Start));
            var hours = (country.Max(e => e.End) - country.Min(e => e.Start)) / 3600.0;
            var detail = string.Join(" · ", new[]
            {
                "Seen by IODA in " + string.Join(", ", sources.Select(SourceLabel)),
                $"began {began.ToLocalGwsDateTime()}",
                hours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"lasting {hours:0} h") : "under an hour",
                measured == 0 ? "Google traffic alone is often noise" : null
            }.Where(part => part is not null));
            outages.Add(new HazardFeature($"ioda-{country.Key}", HazardKinds.Internet, $"Internet outage - {place.Name}", detail,
                place.Latitude, place.Longitude, severity,
                $"https://ioda.inetintel.cc.gatech.edu/country/{Uri.EscapeDataString(country.Key)}", began));
        }
        return outages;
    }

    public static string SourceLabel(string source) => source.ToLowerInvariant() switch
    {
        "bgp" => "routing (BGP)",
        "ping-slash24" => "active probing",
        "merit-nt" => "network telescope",
        GoogleTraffic => "Google traffic",
        _ => source
    };

    private static int SourceOrder(string source) => source.ToLowerInvariant() switch
    {
        "bgp" => 0,
        "ping-slash24" => 1,
        "merit-nt" => 2,
        _ => 3
    };

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
