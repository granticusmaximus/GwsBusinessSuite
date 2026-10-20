using System.Globalization;
using System.Net;
using System.Text.Json;

namespace GwsBusinessSuite.WorldNewsKit;

public sealed record GdeltResult(IReadOnlyList<WorldArticle> Articles, string? Note);

// GDELT's DOC 2.0 API: worldwide coverage in 65 languages with the outlet's real URL, language
// and country, free and key-less (verified live 20-10-26: an English query returned Turkish,
// Russian, Bulgarian and German reports of the same flood). It allows one request every 5
// seconds and answers faster callers with HTTP 429 for a while, so every call goes through one
// paced gate, and a 429 pauses GDELT for 15 minutes rather than retrying into the penalty.
public sealed class GdeltDocClient(HttpClient http, TimeProvider time, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxLookback = TimeSpan.FromHours(24);
    private const string Url = "https://api.gdeltproject.org/api/v2/doc/doc";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((wait, ct) => Task.Delay(wait, time, ct));
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private DateTimeOffset _coolingUntil = DateTimeOffset.MinValue;

    public bool IsCoolingDown => time.GetUtcNow() < _coolingUntil;

    // Articles GDELT has seen since `since` (at most a day back, 250 per call, newest first).
    public async Task<GdeltResult> SearchAsync(string query, DateTimeOffset since, CancellationToken ct)
    {
        if (IsCoolingDown) return new([], "GDELT asked for fewer requests; it's paused for a while.");
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastCall + Spacing - time.GetUtcNow();
            if (wait > TimeSpan.Zero) await _delay(wait, ct);
            var now = time.GetUtcNow();
            var start = since < now - MaxLookback ? now - MaxLookback : since;
            var url = string.Create(CultureInfo.InvariantCulture,
                $"{Url}?query={Uri.EscapeDataString(query)}&mode=artlist&maxrecords=250&format=json&sort=datedesc&startdatetime={start.UtcDateTime:yyyyMMddHHmmss}");
            using var response = await http.GetAsync(url, ct);
            _lastCall = time.GetUtcNow();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _coolingUntil = _lastCall + Cooldown;
                return new([], "GDELT asked for fewer requests; it's paused for a while.");
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return new([], $"GDELT answered HTTP {(int)response.StatusCode}.");
            // GDELT reports a bad query as plain text with HTTP 200.
            if (!body.TrimStart().StartsWith('{')) return new([], "GDELT: " + body.Trim()[..Math.Min(200, body.Trim().Length)]);
            return new(Parse(body), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return new([], $"GDELT couldn't be read: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // {"articles":[{"url","url_mobile","title","seendate":"20261018T174500Z","socialimage","domain","language","sourcecountry"}]}
    public static IReadOnlyList<WorldArticle> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("articles", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var articles = new List<WorldArticle>();
        foreach (var a in items.EnumerateArray())
        {
            var url = Text(a, "url");
            var title = ArticleNormalizer.CleanTitle(Text(a, "title") ?? string.Empty);
            if (url is null || title.Length == 0) continue;
            var seenAt = DateTimeOffset.TryParseExact(Text(a, "seendate"), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var seen) ? seen : DateTimeOffset.UtcNow;
            articles.Add(new WorldArticle(ArticleNormalizer.IdFor(url), url, title, Text(a, "domain") ?? string.Empty,
                Text(a, "language") ?? string.Empty, Text(a, "sourcecountry") ?? string.Empty, seenAt, "gdelt"));
        }
        return articles;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
}
