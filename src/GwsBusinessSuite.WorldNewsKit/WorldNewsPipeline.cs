using System.Text.Json;
using System.Xml;
using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.WorldNewsKit;

public sealed record WorldNewsOptions
{
    public string EmbeddingModel { get; init; } = "embeddinggemma";
    public double JoinThreshold { get; init; } = EventClusterer.DefaultThreshold;
    public double MergeThreshold { get; init; } = EventClusterer.DefaultMergeThreshold;
    public TimeSpan EventWindow { get; init; } = EventClusterer.DefaultWindow;
    public double SituationThreshold { get; init; } = SituationLinker.DefaultThreshold;
    public TimeSpan SituationWindow { get; init; } = SituationLinker.DefaultWindow;
    public TimeSpan KeepFor { get; init; } = TimeSpan.FromDays(30);
    // How far back GDELT is asked on the first run (later runs ask from the last success, so a
    // Mac that slept catches up, up to GDELT's one-day lookback).
    public TimeSpan FirstLookback { get; init; } = TimeSpan.FromHours(6);
    public IReadOnlyList<NewsEdition> Editions { get; init; } = GoogleNewsFeed.Editions;
    public IReadOnlyList<NewsOutlet> Outlets { get; init; } = OutletFeeds.Outlets;
    // Feeds added on the sources page and feeds paused there (13-11-26), asked for each read.
    public Func<IReadOnlyList<NewsOutlet>>? AddedOutlets { get; init; }
    public Func<IReadOnlySet<string>>? PausedFeeds { get; init; }
    // Outlet feeds are read this many at a time (several share a host, such as the BBC's).
    public int OutletConcurrency { get; init; } = 6;
    // WHO's Disease Outbreak News (WhoOutbreakNews), read with the outlets' feeds. Off unless the
    // host turns it on, so a test's pipeline asks no one it wasn't told to.
    public bool ReadWhoOutbreakNews { get; init; }
    // Wikipedia's Current events portal as an early signal (13-11-26). Off unless the host turns it on.
    public bool ReadWikipediaCurrentEvents { get; init; }
    public IReadOnlyList<string> GdeltQueries { get; init; } = [];
    // Searches for stories that are surging (FollowUpQueries, 13-11-26), asked for each read.
    public Func<IReadOnlyList<string>>? FollowUpQueries { get; init; }
    public int EmbedBatchSize { get; init; } = 64;
    // Taken for each embedding batch and released after it, so a host can queue embedding with its
    // other model work (the server's one-at-a-time Ollama scheduler) instead of running alongside it.
    public Func<CancellationToken, Task<IAsyncDisposable>>? EmbedSlot { get; init; }
    // Caps one cycle's embedding work, so a backlog (the first read is about 6,000 reports) is
    // spread over several cycles instead of holding a small server's CPU for minutes.
    public int MaxEmbedPerCycle { get; init; } = int.MaxValue;
    // True while the Mac app is embedding the headlines with the same model (30-10-26,
    // WorldNewsEmbedRelay): a read then leaves embedding to it and groups what has arrived.
    public Func<bool>? EmbedElsewhere { get; init; }
}

public sealed record CycleReport(int Fetched, int Added, int Embedded, int NewEvents, int UpdatedEvents, int MergedEvents, IReadOnlyList<string> Notes,
    int LinkedEvents = 0, int SourcesAttempted = 0, int SourcesSucceeded = 0, int EmbeddingBacklog = 0,
    DateTimeOffset? LatestReportAt = null)
{
    public int Deduplicated => Math.Max(0, Fetched - Added);
}

// One refresh: read every feed (Google News editions, then outlets' own feeds), store the new
// reports, embed their headlines with the local model, and group them into events. Only
// headlines are embedded, outlets' included: the grouping thresholds were tuned on headlines. Feed failures are noted and skipped so one bad edition never stops a
// cycle; without Ollama the reports wait, unembedded, for the next one.
public sealed class WorldNewsPipeline(WorldNewsStore store, HttpClient http, GdeltDocClient gdelt, OllamaClient ollama,
    TimeProvider time, WorldNewsOptions options)
{
    public async Task<CycleReport> RunCycleAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        var fetched = 0;
        var added = 0;
        var sourcesAttempted = 0;
        var sourcesSucceeded = 0;

        foreach (var edition in options.Editions)
        {
            sourcesAttempted++;
            try
            {
                var articles = GoogleNewsFeed.Parse(await http.GetStringAsync(edition.Url, ct), edition);
                sourcesSucceeded++;
                fetched += articles.Count;
                added += store.AddArticles(articles);
            }
            catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                notes.Add($"Google News {edition.Country}: {ex.Message}");
            }
        }

        // Only the feeds that are due (FeedSchedule, 13-11-26): a quiet feed is read less often, a
        // refusing one is left alone for a day, and an unchanged one answers 304 for almost nothing.
        var readAt = time.GetUtcNow();
        var health = store.FeedHealth();
        var paused = options.PausedFeeds?.Invoke() ?? new HashSet<string>();
        var outlets = options.Outlets.Concat(options.AddedOutlets?.Invoke() ?? []).DistinctBy(outlet => outlet.Url)
            .Where(outlet => !paused.Contains(outlet.Url)).ToList();
        var due = outlets.Where(outlet => FeedSchedule.ShouldRead(health.GetValueOrDefault(outlet.Url), readAt)).ToList();
        var outletReads = new (IReadOnlyList<WorldArticle>? Articles, string? Note, FeedHealth Health, int Status, string? ETag, string? Modified)[due.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, due.Count),
            new ParallelOptions { MaxDegreeOfParallelism = options.OutletConcurrency, CancellationToken = ct }, async (i, token) =>
            {
                var outlet = due[i];
                var state = health.GetValueOrDefault(outlet.Url) ?? new FeedHealth(outlet.Url);
                try
                {
                    var read = await ReadOutletAsync(outlet, state, token);
                    outletReads[i] = (read.Articles, null, state, read.Status, read.ETag, read.Modified);
                }
                catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException && !token.IsCancellationRequested)
                {
                    var status = ex is HttpRequestException { StatusCode: { } code } ? (int)code : (int?)null;
                    outletReads[i] = (null, $"{outlet.Name} ({outlet.Language}): {ex.Message}", FeedSchedule.AfterFailure(state, status, ex.Message, readAt), status ?? 0, null, null);
                }
            });
        var healthAfter = new List<FeedHealth>(due.Count);
        foreach (var (articles, note, state, status, etag, modified) in outletReads)
        {
            sourcesAttempted++;
            if (note is not null) notes.Add(note);
            if (articles is null) { healthAfter.Add(state); continue; }
            sourcesSucceeded++;
            fetched += articles.Count;
            var newHere = store.AddArticles(articles);
            added += newHere;
            healthAfter.Add(FeedSchedule.AfterSuccess(state, status, newHere, etag, modified, readAt));
        }
        store.SaveFeedHealth(healthAfter);
        if (due.Count < outlets.Count) notes.Add($"{outlets.Count - due.Count} quiet or paused feeds skipped this read.");

        if (options.ReadWhoOutbreakNews)
        {
            sourcesAttempted++;
            try
            {
                var articles = WhoOutbreakNews.Parse(await http.GetStringAsync(WhoOutbreakNews.Url, ct), time.GetUtcNow());
                sourcesSucceeded++;
                fetched += articles.Count;
                added += store.AddArticles(articles);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                notes.Add($"WHO outbreak news: {ex.Message}");
            }
        }

        if (options.ReadWikipediaCurrentEvents)
        {
            sourcesAttempted++;
            var wikiHealth = store.FeedHealth().GetValueOrDefault(WikipediaCurrentEvents.Url) ?? new FeedHealth(WikipediaCurrentEvents.Url);
            if (FeedSchedule.ShouldRead(wikiHealth, readAt))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, WikipediaCurrentEvents.Url);
                    FeedSchedule.AddValidators(request, wikiHealth);
                    using var response = await http.SendAsync(request, ct);
                    if (FeedSchedule.IsNotModified(response.StatusCode))
                        store.SaveFeedHealth([FeedSchedule.AfterSuccess(wikiHealth, 304, 0, null, null, readAt)]);
                    else if (!response.IsSuccessStatusCode)
                        store.SaveFeedHealth([FeedSchedule.AfterFailure(wikiHealth, (int)response.StatusCode, $"HTTP {(int)response.StatusCode}", readAt)]);
                    else
                    {
                        var entries = WikipediaCurrentEvents.Parse(await response.Content.ReadAsStringAsync(ct), readAt);
                        var newHere = store.AddArticles(entries);
                        sourcesSucceeded++;
                        fetched += entries.Count;
                        added += newHere;
                        store.SaveFeedHealth([FeedSchedule.AfterSuccess(wikiHealth, (int)response.StatusCode, newHere,
                            response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R"), readAt)]);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    notes.Add($"Wikipedia Current events: {ex.Message}");
                    store.SaveFeedHealth([FeedSchedule.AfterFailure(wikiHealth, null, ex.Message, readAt)]);
                }
            }
        }

        // Surging stories are followed with their own searches, at most three a read, after the
        // configured ones (GDELT allows one request every five seconds).
        var followUps = (options.FollowUpQueries?.Invoke() ?? []).Take(WorldNewsKit.FollowUpQueries.MaxPerRead).ToList();
        foreach (var query in options.GdeltQueries.Concat(followUps).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sourcesAttempted++;
            var feed = "gdelt:" + query;
            var startedAt = time.GetUtcNow();
            var from = (store.LastFetched(feed) ?? startedAt - options.FirstLookback) - TimeSpan.FromMinutes(15);
            var result = await gdelt.SearchAsync(query, from, ct);
            if (result.Note is not null)
            {
                notes.Add(result.Note);
                if (gdelt.IsCoolingDown) break;
                continue;
            }
            sourcesSucceeded++;
            fetched += result.Articles.Count;
            added += store.AddArticles(result.Articles);
            store.SetLastFetched(feed, startedAt);
        }

        var embedded = 0;
        try
        {
            IReadOnlyList<WorldArticle> batch;
            var embedSince = time.GetUtcNow() - options.EventWindow;
            while (options.EmbedElsewhere?.Invoke() != true && embedded < options.MaxEmbedPerCycle
                   && (batch = store.RecentArticlesToEmbed(Math.Min(options.EmbedBatchSize, options.MaxEmbedPerCycle - embedded), embedSince)).Count > 0)
            {
                IReadOnlyList<float[]> vectors;
                var slot = options.EmbedSlot is null ? null : await options.EmbedSlot(ct);
                try
                {
                    vectors = await ollama.EmbedAsync(options.EmbeddingModel, batch.Select(a => a.Title).ToList(), ct);
                }
                finally
                {
                    if (slot is not null) await slot.DisposeAsync();
                }
                store.SaveEmbeddings(batch.Select((a, i) => (a.Id, vectors[i])).ToList());
                embedded += batch.Count;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            notes.Add($"Local Ollama couldn't embed reports ({options.EmbeddingModel}): {ex.Message}");
        }

        var grouped = Group();
        store.Prune(time.GetUtcNow() - options.KeepFor);
        store.ReleaseStaleVectors(time.GetUtcNow());

        var retainedSince = time.GetUtcNow() - options.KeepFor;
        return new CycleReport(fetched, added, embedded, grouped.Clustering.Created, grouped.Clustering.Touched.Count - grouped.Clustering.Created,
            grouped.Merged, notes, grouped.Linked, sourcesAttempted, sourcesSucceeded, store.RecentEmbeddingBacklog(retainedSince),
            store.LatestReportAt(retainedSince));
    }

    // One outlet feed, conditionally: 304 means nothing changed. A sitemap index is followed to its
    // first three sitemaps.
    private async Task<(int Status, IReadOnlyList<WorldArticle> Articles, string? ETag, string? Modified)> ReadOutletAsync(
        NewsOutlet outlet, FeedHealth health, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, outlet.Url);
        FeedSchedule.AddValidators(request, health);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (FeedSchedule.IsNotModified(response.StatusCode)) return (304, [], null, null);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        var etag = response.Headers.ETag?.ToString();
        var modified = response.Content.Headers.LastModified?.ToString("R");
        var now = time.GetUtcNow();
        using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct));
        var document = OutletFeeds.Load(body);
        var children = OutletFeeds.SitemapChildren(document);
        if (children.Count == 0) return ((int)response.StatusCode, OutletFeeds.Parse(document, outlet, now), etag, modified);
        var articles = new List<WorldArticle>();
        foreach (var child in children.Take(3))
        {
            using var childBody = new MemoryStream(await http.GetByteArrayAsync(child, ct));
            articles.AddRange(OutletFeeds.Parse(OutletFeeds.Load(childBody), outlet, now));
        }
        return ((int)response.StatusCode, articles, etag, modified);
    }

    // Groups the embedded reports not yet in an event, merges twin events and places events in
    // situations. Part of every read, and run on its own when the Mac app sends embeddings.
    public (ClusterResult Clustering, int Merged, int Linked) Group()
    {
        var now = time.GetUtcNow();
        var since = now - options.EventWindow;
        var events = store.ActiveEvents(since);
        var clustering = EventClusterer.Assign(store.EmbeddedUnassigned(), events, store.GroupHintEvents(since),
            options.JoinThreshold, options.EventWindow, () => Guid.NewGuid().ToString("N"));
        store.SaveClustering(clustering);
        var merges = EventClusterer.MergeTwins(events, clustering.Touched.ToHashSet(), options.MergeThreshold);
        store.ApplyMerges(merges);
        // Events that reached a situation's size are placed in one (the first read after an update
        // places every recent event, oldest first).
        var situationSince = now - options.SituationWindow;
        var links = SituationLinker.Link(store.EventsToLink(situationSince, SituationLinker.MinArticles),
            store.ActiveSituations(situationSince), options.SituationThreshold, options.SituationWindow, () => Guid.NewGuid().ToString("N"));
        store.SaveLinks(links);
        return (clustering, merges.Count, links.Count);
    }
}
