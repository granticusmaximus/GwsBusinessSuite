using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.MessageSigns;

// Caltrans changeable message signs - the same key-less CWWP2 open-data host as the Caltrans
// cameras, one JSON file per district (cms/cmsStatusD{nn}.json). Verified live 2026-10-09: all
// 12 districts answer, 1,017 signs, 417 showing a message. Each sign has up to two phases of
// three lines; empty or unreported lines read "Not Reported", and out-of-service signs keep
// their last message, so both are filtered out.
public sealed class CaltransMessageSignProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<CaltransMessageSignProvider> logger) : IMessageSignProvider
{
    private const string CacheKey = "message-signs:caltrans";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public string SourceName => "Caltrans";

    public BoundingBox Coverage { get; } = new(North: 42.1, South: 32.4, East: -114.0, West: -124.5);

    public async Task<IReadOnlyList<MessageSign>> GetActiveSignsAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<MessageSign>? cached) && cached is not null) return cached;

        var districts = await Task.WhenAll(Enumerable.Range(1, 12).Select(d => FetchDistrictAsync(d, cancellationToken)));
        var signs = districts.SelectMany(d => d).ToList();
        if (signs.Count > 0) cache.Set(CacheKey, (IReadOnlyList<MessageSign>)signs, CacheDuration);
        return signs;
    }

    private async Task<IReadOnlyList<MessageSign>> FetchDistrictAsync(int district, CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<CmsResponse>(
                $"data/d{district}/cms/cmsStatusD{district:00}.json", cancellationToken);
            var signs = new List<MessageSign>();
            foreach (var record in response?.Data ?? [])
            {
                var cms = record.Cms;
                if (cms?.Location is not { } location || cms.Message is not { } message) continue;
                if (!string.Equals(cms.InService, "true", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(message.Display, "Blank", StringComparison.OrdinalIgnoreCase)) continue;
                if (!double.TryParse(location.Latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                    || !double.TryParse(location.Longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                {
                    continue;
                }

                var pages = new[]
                    {
                        SignText.CleanLines([message.Phase1?.Line1, message.Phase1?.Line2, message.Phase1?.Line3]),
                        SignText.CleanLines([message.Phase2?.Line1, message.Phase2?.Line2, message.Phase2?.Line3])
                    }
                    .Where(page => page.Count > 0)
                    .ToList();
                if (pages.Count == 0) continue;

                signs.Add(new MessageSign(
                    Id: $"caltrans-{district}-{cms.Index}",
                    Name: string.IsNullOrWhiteSpace(location.NearbyPlace) ? location.LocationName ?? "Caltrans sign" : $"{location.LocationName} ({location.NearbyPlace})",
                    Latitude: lat,
                    Longitude: lon,
                    Road: location.Route,
                    Direction: string.IsNullOrWhiteSpace(location.Direction) ? null : location.Direction,
                    Pages: pages,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://quickmap.dot.ca.gov"));
            }
            return signs;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to fetch Caltrans message signs for district {District}.", district);
            return [];
        }
    }

    private sealed record CmsResponse([property: JsonPropertyName("data")] List<CmsRecord>? Data);

    private sealed record CmsRecord([property: JsonPropertyName("cms")] Cms? Cms);

    private sealed record Cms(
        [property: JsonPropertyName("index")] string? Index,
        [property: JsonPropertyName("location")] CmsLocation? Location,
        [property: JsonPropertyName("inService")] string? InService,
        [property: JsonPropertyName("message")] CmsMessage? Message);

    private sealed record CmsLocation(
        [property: JsonPropertyName("locationName")] string? LocationName,
        [property: JsonPropertyName("nearbyPlace")] string? NearbyPlace,
        [property: JsonPropertyName("latitude")] string? Latitude,
        [property: JsonPropertyName("longitude")] string? Longitude,
        [property: JsonPropertyName("direction")] string? Direction,
        [property: JsonPropertyName("route")] string? Route);

    private sealed record CmsMessage(
        [property: JsonPropertyName("display")] string? Display,
        [property: JsonPropertyName("phase1")] CmsPhase1? Phase1,
        [property: JsonPropertyName("phase2")] CmsPhase2? Phase2);

    private sealed record CmsPhase1(
        [property: JsonPropertyName("phase1Line1")] string? Line1,
        [property: JsonPropertyName("phase1Line2")] string? Line2,
        [property: JsonPropertyName("phase1Line3")] string? Line3);

    private sealed record CmsPhase2(
        [property: JsonPropertyName("phase2Line1")] string? Line1,
        [property: JsonPropertyName("phase2Line2")] string? Line2,
        [property: JsonPropertyName("phase2Line3")] string? Line3);
}
