using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// HIBP's /api/v3/breaches - the public list of breached sites (~1,000 entries, confirmed live
// 2026-10-04). No key is needed for this endpoint (only account searches need one), but HIBP
// rejects requests without a User-Agent, which the typed client's registration sets.
public sealed class DataBreachFeedService(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<DataBreachFeedService> logger) : IDataBreachFeedService
{
    private const string BreachesUrl = "https://haveibeenpwned.com/api/v3/breaches";
    private const string CacheKey = "threat-intel:hibp-breaches";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    public async Task<IReadOnlyList<DataBreach>> GetRecentBreachesAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<DataBreach>? breaches) || breaches is null)
        {
            try
            {
                await using var stream = await httpClient.GetStreamAsync(BreachesUrl, cancellationToken);
                var rows = await JsonSerializer.DeserializeAsync<List<BreachRow>>(stream, cancellationToken: cancellationToken) ?? [];
                breaches = rows
                    .Where(row => !row.IsFabricated && !row.IsSpamList && !string.IsNullOrWhiteSpace(row.Name))
                    .Select(ToBreach)
                    .OrderByDescending(breach => breach.AddedDate)
                    .ToList();
                cache.Set(CacheKey, breaches, CacheDuration);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogWarning(ex, "Failed to fetch the HIBP breach list");
                return [];
            }
        }
        return breaches.Take(Math.Clamp(limit, 1, 200)).ToList();
    }

    private static DataBreach ToBreach(BreachRow row) => new(
        row.Name!,
        string.IsNullOrWhiteSpace(row.Title) ? row.Name! : row.Title,
        string.IsNullOrWhiteSpace(row.Domain) ? null : row.Domain,
        DateOnly.TryParseExact(row.BreachDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
        DateTimeOffset.TryParse(row.AddedDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var added) ? added : DateTimeOffset.MinValue,
        row.PwnCount,
        row.DataClasses ?? [],
        row.IsVerified,
        $"https://haveibeenpwned.com/Breach/{Uri.EscapeDataString(row.Name!)}");

    private sealed class BreachRow
    {
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("Title")] public string? Title { get; set; }
        [JsonPropertyName("Domain")] public string? Domain { get; set; }
        [JsonPropertyName("BreachDate")] public string? BreachDate { get; set; }
        [JsonPropertyName("AddedDate")] public string? AddedDate { get; set; }
        [JsonPropertyName("PwnCount")] public long PwnCount { get; set; }
        [JsonPropertyName("DataClasses")] public List<string>? DataClasses { get; set; }
        [JsonPropertyName("IsVerified")] public bool IsVerified { get; set; }
        [JsonPropertyName("IsFabricated")] public bool IsFabricated { get; set; }
        [JsonPropertyName("IsSpamList")] public bool IsSpamList { get; set; }
    }
}
