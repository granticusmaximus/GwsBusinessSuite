using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// World News features (20-11-26): catch me up, the morning briefing and watchlist dossiers in
// Sentinel, people and organisation pages, and front pages by country.
public sealed partial class WorldNewsService
{
    public static string StoryLink(string eventId) => $"/admin/world-news/events/{Uri.EscapeDataString(eventId)}";

    // ---- 1. Catch me up ----

    private readonly MemoryCache _catchUps = new(new MemoryCacheOptions { SizeLimit = 200 });

    // What the reports since someone last read the story add, in a few sentences. Null with a
    // reason when there's nothing new or the model can't answer.
    public async Task<(string? Text, string? Problem)> CatchUpAsync(string eventId, DateTimeOffset since, CancellationToken ct = default)
    {
        var key = $"{eventId}:{since.ToUnixTimeSeconds()}";
        if (_catchUps.TryGetValue(key, out string? cached) && cached is not null) return (cached, null);
        var story = await Task.Run(() => Store.Event(eventId), ct);
        if (story is null) return (null, "This story isn't kept any more.");
        var newReports = (await Task.Run(() => Store.ArticlesForEvent(eventId), ct)).Where(a => a.SeenAt > since).ToList();
        if (newReports.Count == 0) return (null, "No new reports since you last read it.");
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            using var shared = MacRelayScope.ForSharedWork();
            await using var scope = _scopes.CreateAsyncScope();
            var raw = await scope.ServiceProvider.GetRequiredService<IOllamaService>().GenerateAsync(_model, CatchUp.SystemPrompt,
                CatchUp.Prompt(story.Headline ?? story.Title, since, newReports, at => at.ToLocalGwsDateTime()), limit.Token);
            var text = System.Text.RegularExpressions.Regex.Replace(raw ?? string.Empty, "<think>.*?</think>", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline).Trim();
            if (text.Length == 0) return (null, "The AI model gave no answer. Try again in a minute.");
            _catchUps.Set(key, text, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
            return (text, null);
        }
        catch (ServerAiUnavailableException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "World News: catch-up failed for {EventId}", eventId);
            return (null, "The AI model didn't answer just now. Try again in a minute.");
        }
    }

    // ---- 2. The morning briefing and 6. watchlist dossiers ----

    private DateTimeOffset _lastPersonalPages = DateTimeOffset.MinValue;

    // Hourly from the read: each subscriber's briefing once a morning, every watchlist's dossier.
    public async Task<int> WritePersonalPagesIfDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (now - _lastPersonalPages < TimeSpan.FromHours(1)) return 0;
        _lastPersonalPages = now;
        var written = 0;
        var nowLocal = now.ToLocalTime();
        foreach (var (username, lastBuilt) in await Task.Run(Store.BriefingSubscribers, ct))
        {
            if (!MorningBriefing.Due(lastBuilt?.ToLocalTime(), nowLocal)) continue;
            try
            {
                await BuildMorningBriefingAsync(username, ct);
                written++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "World News: morning briefing failed for a reader");
            }
        }
        foreach (var username in await Task.Run(Store.KeywordWatchers, ct))
        {
            try
            {
                written += await WriteDossiersAsync(username, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "World News: watchlist dossiers failed for a reader");
            }
        }
        return written;
    }

    public async Task<Guid> BuildMorningBriefingAsync(string username, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-1);
        var world = await Task.Run(() => Store.World(since, storiesPerCountry: 1, biggestStories: MorningBriefing.LeadStories), ct);
        var leads = world.BiggestStories.Select(s => new MorningBriefing.Lead(s.Event, Store.LatestSummary(s.Event.Id)?.Text)).ToList();
        var watches = Store.KeywordWatches(username)
            .Select(w => (w.Query, (IReadOnlyList<EventOverview>)Store.StoriesMatching(w.Query, since, 5)))
            .ToList();
        var markdown = MorningBriefing.Markdown(now.ToLocalTime(), leads, watches, Store.Silences(now), StoryLink);
        var id = await WritePersonalPageAsync(MorningBriefing.PageSystemKey, username, MorningBriefing.PageTitle, "🌍", markdown, ct);
        Store.MarkBriefingBuilt(username, now);
        return id;
    }

    private async Task<int> WriteDossiersAsync(string username, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var written = 0;
        foreach (var watch in Store.KeywordWatches(username))
        {
            var stories = await Task.Run(() => Store.StoriesMatching(watch.Query, now.AddDays(-WatchDossier.Window), WatchDossier.MaxStories), ct);
            await WritePersonalPageAsync(WatchDossier.KeyFor(watch.Query), username, WatchDossier.Title(watch.Query), "🗂️",
                WatchDossier.Markdown(watch.Query, stories, now.ToLocalTime(), StoryLink), ct);
            written++;
        }
        return written;
    }

    private async Task<Guid> WritePersonalPageAsync(string key, string username, string title, string icon, string markdown, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await SentinelPersonalPages.WriteAsync(db, scope.ServiceProvider.GetRequiredService<IWikiService>(), key, username, title, icon, markdown, ct);
    }

    // ---- 3. People and organisation pages ----

    public sealed record EntityFacts(string Label, string? Description, string? WikidataUrl, string? WikipediaUrl);

    private readonly MemoryCache _entities = new(new MemoryCacheOptions { SizeLimit = 500 });
    public const string WikidataApi = "https://www.wikidata.org/w/api.php";

    // Who or what a name is, from Wikidata's search (no key): the best match's label and description.
    public async Task<EntityFacts?> LookUpEntityAsync(string name, CancellationToken ct = default)
    {
        var query = (name ?? string.Empty).Trim();
        if (query.Length is < 2 or > 120) return null;
        if (_entities.TryGetValue(query.ToLowerInvariant(), out EntityFacts? cached)) return cached;
        EntityFacts? facts = null;
        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"{WikidataApi}?action=wbsearchentities&format=json&language=en&uselang=en&type=item&limit=1&search={Uri.EscapeDataString(query)}";
            using var response = await http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode) facts = ParseEntitySearch(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "World News: Wikidata lookup failed");
        }
        _entities.Set(query.ToLowerInvariant(), facts, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) });
        return facts;
    }

    public static EntityFacts? ParseEntitySearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("search", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        foreach (var hit in results.EnumerateArray())
        {
            var id = hit.TryGetProperty("id", out var i) ? i.GetString() : null;
            var label = hit.TryGetProperty("label", out var l) ? l.GetString() : null;
            if (id is null || string.IsNullOrWhiteSpace(label)) continue;
            var description = hit.TryGetProperty("description", out var d) ? d.GetString() : null;
            return new EntityFacts(label, description, $"https://www.wikidata.org/wiki/{id}",
                $"https://en.wikipedia.org/wiki/Special:Search?search={Uri.EscapeDataString(label)}");
        }
        return null;
    }

    public IReadOnlyList<EventOverview> StoriesAbout(string name, int days = 30, int limit = 40) =>
        Store.StoriesMatching("\"" + name.Replace("\"", string.Empty).Trim() + "\"", DateTimeOffset.UtcNow.AddDays(-days), limit);

    // ---- 4. Front pages compared ----

    public IReadOnlyList<FrontPage> FrontPagesNow(int hours = 12) =>
        Store.FrontPagesSince(DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours, 1, 48)), FrontPages.Countries);
}
