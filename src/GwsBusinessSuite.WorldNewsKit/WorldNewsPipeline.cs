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
    // Outlet feeds are read this many at a time (several share a host, such as the BBC's).
    public int OutletConcurrency { get; init; } = 6;
    public IReadOnlyList<string> GdeltQueries { get; init; } = [];
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
    int LinkedEvents = 0);

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

        foreach (var edition in options.Editions)
        {
            try
            {
                var articles = GoogleNewsFeed.Parse(await http.GetStringAsync(edition.Url, ct), edition);
                fetched += articles.Count;
                added += store.AddArticles(articles);
            }
            catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                notes.Add($"Google News {edition.Country}: {ex.Message}");
            }
        }

        var outletReads = new (IReadOnlyList<WorldArticle>? Articles, string? Note)[options.Outlets.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, options.Outlets.Count),
            new ParallelOptions { MaxDegreeOfParallelism = options.OutletConcurrency, CancellationToken = ct }, async (i, token) =>
            {
                var outlet = options.Outlets[i];
                try
                {
                    using var body = new MemoryStream(await http.GetByteArrayAsync(outlet.Url, token));
                    outletReads[i] = (OutletFeeds.Parse(body, outlet, time.GetUtcNow()), null);
                }
                catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException && !token.IsCancellationRequested)
                {
                    outletReads[i] = (null, $"{outlet.Name} ({outlet.Language}): {ex.Message}");
                }
            });
        foreach (var (articles, note) in outletReads)
        {
            if (note is not null) notes.Add(note);
            if (articles is null) continue;
            fetched += articles.Count;
            added += store.AddArticles(articles);
        }

        foreach (var query in options.GdeltQueries)
        {
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

        return new CycleReport(fetched, added, embedded, grouped.Clustering.Created, grouped.Clustering.Touched.Count - grouped.Clustering.Created,
            grouped.Merged, notes, grouped.Linked);
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
