using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Hazards;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record TravelAdvisory(string Code, string Country, int Level, string Advice, string Url, DateTimeOffset? Updated);

// Travel advice for Overwatch (07-11-26): the US State Department's level for each country, from
// its public JSON (no key). Level 1, normal precautions, covers most of the world and isn't drawn;
// levels 2 to 4 shade the country and pin its centre with the advice. The feed tags countries with
// FIPS codes ("YM" is Yemen), so the country is found by the name in each title instead.
public sealed partial class TravelAdvisoryService(HttpClient httpClient, IMemoryCache cache, ILogger<TravelAdvisoryService> logger)
{
    private const string Url = "https://cadataapi.state.gov/api/TravelAdvisories";
    private const string CacheKey = "overwatch:travel-advisories";

    // The State Department's names that aren't any of a country's names in the places table.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Türkiye"] = "Turkey",
        ["Cabo Verde"] = "Cape Verde",
        ["Republic of the Congo"] = "CG",
        ["Republic of Congo"] = "CG",
        ["Democratic Republic of the Congo"] = "CD",
        ["Burma"] = "Myanmar",
        ["Mainland China"] = "China",
        ["Kingdom of Denmark"] = "Denmark",
        ["Naoero"] = "Nauru",
        ["Kyrgyz Republic"] = "Kyrgyzstan",
        ["Côte d’Ivoire"] = "CI",
        ["Côte d'Ivoire"] = "CI",
        ["Federated States of Micronesia"] = "FM",
        ["Timor-Leste"] = "TL",
        ["Gaza"] = "PS",
        ["West Bank"] = "PS",
    };

    public async Task<IReadOnlyList<TravelAdvisory>> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<TravelAdvisory>? cached) && cached is not null) return cached;
        try
        {
            var advisories = Parse(await httpClient.GetStringAsync(Url, ct), WorldNewsPlaces.Instance);
            cache.Set(CacheKey, advisories, TimeSpan.FromHours(6));
            return advisories;
        }
        catch (HostPausedException ex)
        {
            logger.LogDebug(ex, "The State Department's travel advisories are paused after refusing us");
            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Travel advice layer: the State Department request failed");
            return [];
        }
    }

    // One advisory per country, the highest level when a title names a place twice.
    public static IReadOnlyList<TravelAdvisory> Parse(string json, WorldNewsPlaces places)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var byCode = new Dictionary<string, TravelAdvisory>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var match = TitleShape().Match(Text(item, "Title") ?? string.Empty);
            if (!match.Success) continue;
            if (Resolve(match.Groups["country"].Value, places) is not { } country) continue;
            var level = match.Groups["level"].Value[0] - '0';
            var updated = DateTimeOffset.TryParse(Text(item, "Updated") ?? Text(item, "Published"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
            var advisory = new TravelAdvisory(country.Code, country.Name, level, match.Groups["advice"].Value.Trim(),
                Text(item, "Link") ?? "https://travel.state.gov/content/travel/en/traveladvisories/traveladvisories.html", updated);
            if (!byCode.TryGetValue(country.Code, out var existing) || existing.Level < level) byCode[country.Code] = advisory;
        }
        return byCode.Values.OrderByDescending(a => a.Level).ThenBy(a => a.Country, StringComparer.Ordinal).ToList();
    }

    // Pins for levels 2 to 4 at each country's centre: 4 is major, 3 moderate, 2 minor.
    public static IReadOnlyList<HazardFeature> Pins(IEnumerable<TravelAdvisory> advisories, WorldNewsPlaces places) =>
        advisories.Where(a => a.Level >= 2)
            .Select(a => (Advisory: a, Place: places.Country(a.Code)))
            .Where(x => x.Place is not null)
            .Select(x => new HazardFeature($"travel-{x.Advisory.Code}", HazardKinds.Travel,
                string.Create(CultureInfo.InvariantCulture, $"{x.Advisory.Country}: Level {x.Advisory.Level}"),
                $"{x.Advisory.Advice} · US State Department", x.Place!.Latitude, x.Place.Longitude,
                x.Advisory.Level switch { 4 => "major", 3 => "moderate", _ => "minor" }, x.Advisory.Url, x.Advisory.Updated))
            .ToList();

    // "Burma (Myanmar)", "The Bahamas", "Israel, the West Bank and Gaza", "Mexico Travel Advisory":
    // the whole name first, then the part in brackets, the part before them or a comma, and without
    // "The" or "Travel Advisory". Overseas territories (Guadeloupe, Bonaire) aren't countries in the
    // places table, so they aren't placed.
    public static WorldNewsCountry? Resolve(string name, WorldNewsPlaces places)
    {
        name = name.Replace(" Travel Advisory", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var candidates = new List<string> { name };
        var open = name.IndexOf('(', StringComparison.Ordinal);
        if (open > 0)
        {
            candidates.Add(name[(open + 1)..].TrimEnd(')', ' '));
            candidates.Add(name[..open].Trim());
        }
        if (name.IndexOf(',', StringComparison.Ordinal) is var comma and > 0) candidates.Add(name[..comma].Trim());
        candidates.AddRange(candidates.Where(c => c.StartsWith("The ", StringComparison.OrdinalIgnoreCase)).Select(c => c[4..]).ToList());
        foreach (var candidate in candidates)
        {
            var lookup = Aliases.TryGetValue(candidate, out var alias) ? alias : candidate;
            if (places.FindCountry(lookup) is { } country) return country;
        }
        return null;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // "Iran - Level 4: Do Not Travel"
    [GeneratedRegex(@"^(?<country>.+?)\s+[-–]\s+Level\s+(?<level>[1-4])\s*:\s*(?<advice>.+)$")]
    private static partial Regex TitleShape();
}
