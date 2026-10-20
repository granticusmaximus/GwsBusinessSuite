using System.Xml;
using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.WorldNewsKit;

public sealed record WorldNewsOptions
{
    public string EmbeddingModel { get; init; } = "embeddinggemma";
    public double JoinThreshold { get; init; } = EventClusterer.DefaultThreshold;
    public double MergeThreshold { get; init; } = EventClusterer.DefaultMergeThreshold;
    public TimeSpan EventWindow { get; init; } = EventClusterer.DefaultWindow;
    public TimeSpan KeepFor { get; init; } = TimeSpan.FromDays(30);
    // How far back GDELT is asked on the first run (later runs ask from the last success, so a
    // Mac that slept catches up, up to GDELT's one-day lookback).
    public TimeSpan FirstLookback { get; init; } = TimeSpan.FromHours(6);
    public IReadOnlyList<NewsEdition> Editions { get; init; } = GoogleNewsFeed.Editions;
    public IReadOnlyList<string> GdeltQueries { get; init; } = [];
    public int EmbedBatchSize { get; init; } = 64;
    // Caps one cycle's embedding work, so a backlog (the first read is about 6,000 reports) is
    // spread over several cycles instead of holding a small server's CPU for minutes.
    public int MaxEmbedPerCycle { get; init; } = int.MaxValue;
}

public sealed record CycleReport(int Fetched, int Added, int Embedded, int NewEvents, int UpdatedEvents, int MergedEvents, IReadOnlyList<string> Notes);

// One refresh: read every feed, store the new reports, embed them with the local model, and
// group them into events. Feed failures are noted and skipped so one bad edition never stops a
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
            while (embedded < options.MaxEmbedPerCycle
                   && (batch = store.ArticlesToEmbed(Math.Min(options.EmbedBatchSize, options.MaxEmbedPerCycle - embedded))).Count > 0)
            {
                var vectors = await ollama.EmbedAsync(options.EmbeddingModel, batch.Select(a => a.Title).ToList(), ct);
                store.SaveEmbeddings(batch.Select((a, i) => (a.Id, vectors[i])).ToList());
                embedded += batch.Count;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            notes.Add($"Local Ollama couldn't embed reports ({options.EmbeddingModel}): {ex.Message}");
        }

        var now = time.GetUtcNow();
        var since = now - options.EventWindow;
        var events = store.ActiveEvents(since);
        var clustering = EventClusterer.Assign(store.EmbeddedUnassigned(), events, store.GroupHintEvents(since),
            options.JoinThreshold, options.EventWindow, () => Guid.NewGuid().ToString("N"));
        store.SaveClustering(clustering);
        var merges = EventClusterer.MergeTwins(events, clustering.Touched.ToHashSet(), options.MergeThreshold);
        store.ApplyMerges(merges);
        store.Prune(now - options.KeepFor);

        return new CycleReport(fetched, added, embedded, clustering.Created, clustering.Touched.Count - clustering.Created, merges.Count, notes);
    }
}
