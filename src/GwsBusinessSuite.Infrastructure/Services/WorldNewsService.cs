using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// World News (Intelligence workspace, .claude/plans/world-news-situations-macos-2026-10.md):
// WorldNewsKit's pipeline on the server. 41 Google News country editions and 64 outlets' own
// feeds are read every 15 minutes and grouped into events across languages with embeddinggemma
// on the server's Ollama. embeddinggemma is in ollama/server-models.txt, and Light mode allows
// it, since it's embeddings, not generation. After each read the busiest events get an English
// headline and summary from the same model and service Media Watch summarizes with (summaries
// are what Light mode keeps on the server). The data lives in its own SQLite file beside the
// main database (no migrations), keeping 30 days.
public sealed class WorldNewsService : IDisposable
{
    public const string HttpClientName = "world-news";
    // The last finished read is kept in the news database, so a restart doesn't show "Not read yet".
    private const string LastReadKey = "world-news:last-read";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    // The most events a list holds; the page shows them a page at a time.
    public const int MaxEvents = 300;

    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly OllamaClient _ollama;
    private readonly WorldNewsPipeline _pipeline;
    private readonly EventSummarizer _summarizer;
    private readonly EventListCache _eventLists;
    private readonly RangeListCache<SituationOverview> _situationLists;
    // The World view's snapshot by range, held like the lists (a list of one).
    private readonly RangeListCache<WorldSnapshot> _worldSnapshots;
    private readonly ILogger<WorldNewsService> _logger;
    private readonly WorldNewsOptions _options;
    private readonly WorldNewsMapCache? _mapCache;

    public WorldNewsService(IConfiguration configuration, IOptions<ContentStudioOptions> ollamaOptions,
        IHttpClientFactory httpClientFactory, IServiceScopeFactory scopes, ILogger<WorldNewsService> logger,
        OllamaWorkloadScheduler? workloads = null, WorldNewsMapCache? mapCache = null)
    {
        _mapCache = mapCache;
        _logger = logger;
        var path = Path.GetFullPath(configuration["WorldNews:DatabasePath"] ?? "/app/data/worldnews.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Store = new WorldNewsStore(path);
        var baseUrl = string.IsNullOrWhiteSpace(ollamaOptions.Value.BaseUrl) ? ContentStudioOptions.DefaultBaseUrl : ollamaOptions.Value.BaseUrl;
        _ollama = new OllamaClient(new Uri(baseUrl.TrimEnd('/') + "/"), new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        var http = httpClientFactory.CreateClient(HttpClientName);
        // Embedding takes the server's one Ollama slot a batch at a time, behind interactive work, so
        // the two models no longer run at once and saturate every core (seen 30-10-26: Ollama at
        // 371% of the droplet's 400% while the web app got 3.5%).
        _options = new WorldNewsOptions
        {
            MaxEmbedPerCycle = configuration.GetValue("WorldNews:MaxEmbedPerCycle", 1500),
            EmbedSlot = workloads is null ? null : ct => workloads.AcquireAsync(OllamaWorkloadPriority.Background, ct).AsTask()
        };
        _pipeline = new WorldNewsPipeline(Store, http, new GdeltDocClient(http, TimeProvider.System), _ollama, TimeProvider.System, _options);
        // IOllamaService is a typed HttpClient, so each summary takes a fresh one rather than this
        // singleton holding one for the app's lifetime. 8k context fits SummaryOptions.MaxPromptChars.
        var model = ollamaOptions.Value.Model;
        // Server summaries are the fallback for when the Mac app isn't writing them: one a read, at
        // background priority, and one that's running stops the moment someone starts a chat (it's
        // summarized on a later read), so nobody waits behind it.
        _summarizer = new EventSummarizer(Store, async (system, prompt, ct) =>
            {
                using var standAside = workloads is null ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : CancellationTokenSource.CreateLinkedTokenSource(ct, workloads.InteractiveArrivedToken);
                using var background = workloads?.UseBackgroundPriority();
                await using var scope = scopes.CreateAsyncScope();
                try
                {
                    return await scope.ServiceProvider.GetRequiredService<IOllamaService>().GenerateAsync(model, system, prompt, 8192, standAside.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && standAside.IsCancellationRequested)
                {
                    throw new InvalidOperationException("it stood aside for someone using the AI and will be summarized on a later read");
                }
            }, model, TimeProvider.System, at => at.ToLocalGwsDateTime(),
            new SummaryOptions { PerRun = configuration.GetValue("WorldNews:SummariesPerRead", 1) });
        LastRefreshed = Store.LastFetched(LastReadKey);
        if (LastRefreshed is not null) Status = string.Empty;
        void RebuildFailed(Exception ex) => _logger.LogWarning(ex, "Rebuilding a World News list failed");
        _eventLists = new EventListCache(since => Store.TopEvents(since, MaxEvents), TimeProvider.System, Interval, onError: RebuildFailed);
        _situationLists = new RangeListCache<SituationOverview>(since => Store.TopSituations(since, MaxEvents), TimeProvider.System, Interval, onError: RebuildFailed);
        _worldSnapshots = new RangeListCache<WorldSnapshot>(since => [Store.World(since)], TimeProvider.System, Interval, onError: RebuildFailed);
        // Built at startup, so the first visit after a deploy doesn't wait for it.
        WarmEventLists();
    }

    // The busiest events in the last rangeHours, from memory unless a read has changed them.
    public IReadOnlyList<EventOverview> TopEvents(int rangeHours) => _eventLists.Get(rangeHours);

    // Developing stories with news in the last rangeHours, likewise kept in memory between reads.
    public IReadOnlyList<SituationOverview> TopSituations(int rangeHours) => _situationLists.Get(rangeHours);

    public WorldSnapshot World(int rangeHours) => _worldSnapshots.Get(rangeHours)[0];

    // The list first, so the page has it soon after a deploy; then, once, the covering index a
    // database from before it needs (minutes over the droplet's 2 GB of reports), and the lists
    // rebuilt on it.
    private void WarmEventLists() => _ = Task.Run(() =>
    {
        try { _eventLists.Get(24); }
        catch (Exception ex) { _logger.LogWarning(ex, "Building the world news event list failed"); }
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            if (!Store.EnsureCoveringIndex()) return;
            _logger.LogInformation("World News covering index built in {Seconds:N0} s", started.Elapsed.TotalSeconds);
            ClearLists();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Building the World News covering index failed; it's tried again at the next start"); }
    });

    private void ClearLists(bool rebuild = true)
    {
        _eventLists.Clear(rebuild);
        _situationLists.Clear(rebuild);
        _worldSnapshots.Clear(rebuild);
    }

    public WorldNewsStore Store { get; }
    public string Status { get; private set; } = "Not read yet.";
    public DateTimeOffset? LastRefreshed { get; private set; }
    public bool IsRefreshing => _running.CurrentCount == 0;
    public event Action? Changed;
    public event Action<string, WorldNewsAlert>? AlertCreated;

    // One read. A read already running is left to finish rather than doubled up.
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct)) return;
        try
        {
            Status = "Reading the world's news...";
            Changed?.Invoke();
            // Synchronous SQLite work and CPU-bound grouping: keep it off the caller's thread.
            var report = await Task.Run(() => _pipeline.RunCycleAsync(ct), ct);
            await ClassifySituationsAsync(ct);
            var alerts = Store.CreateWatchAlerts(DateTimeOffset.UtcNow).Concat(CreateSurgeAlerts(DateTimeOffset.UtcNow));
            foreach (var alert in alerts) AlertCreated?.Invoke(alert.Username, alert);
            LastRefreshed = DateTimeOffset.UtcNow;
            Store.SetLastFetched(LastReadKey, LastRefreshed.Value);
            ClearLists();
            // While the Mac app writes summaries with its bigger local model, the server's small
            // one stays out of it; it takes over again if the Mac hasn't asked for work in a while.
            var relayActive = RelayActive(DateTimeOffset.UtcNow);
            Status = relayActive ? "Summaries are being written on the Mac..." : "Summarizing the biggest events...";
            Changed?.Invoke();
            var summaries = relayActive ? new SummaryRunReport(0, []) : await Task.Run(() => _summarizer.RunAsync(ct), ct);
            var notes = report.Notes.Concat(summaries.Notes).ToList();
            var problems = notes.Count == 0 ? string.Empty
                : $" {notes.Count} {(notes.Count == 1 ? "problem" : "problems")}: {notes[0]}";
            Status = $"{report.Added:N0} new reports, {report.NewEvents:N0} new events, {summaries.Written:N0} summaries written.{problems}";
            if (notes.Count > 0)
                _logger.LogWarning("World news read finished with {Count} problem(s): {Notes}", notes.Count, string.Join(" | ", notes));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status = "Reading stopped.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "World news read failed");
            Status = "Couldn't read the news: " + ex.Message;
        }
        finally
        {
            _running.Release();
            // Summaries add English headlines, so the lists are rebuilt once more.
            ClearLists();
            _mapCache?.Clear();
            Changed?.Invoke();
        }
    }

    private const string RelayKey = "world-news:summary-relay";
    // How recently the Mac app must have asked for work for the server's model to stand aside.
    public static readonly TimeSpan RelayFreshness = TimeSpan.FromHours(2);

    // The Mac app (signed in as an admin) asks for events to summarize with its local model.
    // Asking is also how the server knows the Mac is doing it.
    public IReadOnlyList<SummaryJob> SummaryJobsForRelay(int limit)
    {
        Store.SetLastFetched(RelayKey, DateTimeOffset.UtcNow);
        return _summarizer.Jobs(Math.Clamp(limit, 1, 10));
    }

    public string? SaveRelaySummary(string eventId, string answer, string model, int articleCount)
    {
        var error = _summarizer.Save(eventId, answer, $"{model.Trim()} on the Mac", articleCount);
        if (error is null)
        {
            ClearLists(rebuild: false);
            _mapCache?.Clear();
        }
        return error;
    }

    public bool RelayActive(DateTimeOffset now) => Store.LastFetched(RelayKey) is { } at && at >= now - RelayFreshness;

    private IReadOnlyDictionary<string, IReadOnlyList<float[]>>? _kindExamples;

    // What each situation with news in the last fortnight is about, for Overwatch's kind filter.
    // Redone every read: a situation's fingerprint moves as events join it.
    private async Task ClassifySituationsAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - SituationLinker.DefaultWindow;
        var active = Store.ActiveSituations(since);
        if (active.Count == 0) return;
        if (await KindExamplesAsync(ct) is { } examples)
        {
            Store.SaveKinds(active.Select(s => (s.Id, SituationKinds.Classify(s.Sum, examples))).ToList());
            return;
        }
        var text = Store.TopSituations(since, int.MaxValue).ToDictionary(s => s.Id, s => $"{s.Title} {s.Latest}");
        Store.SaveKinds(active.Select(s => (s.Id, SituationKinds.FromWords(text.GetValueOrDefault(s.Id) ?? string.Empty))).ToList(),
            onlyMissing: true);
    }

    // The kinds' example headlines as unit vectors, embedded once per run of the app.
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<float[]>>?> KindExamplesAsync(CancellationToken ct)
    {
        if (_kindExamples is not null) return _kindExamples;
        try
        {
            var examples = SituationKinds.Examples.SelectMany(pair => pair.Value.Select(text => (Kind: pair.Key, Text: text))).ToList();
            var vectors = await _ollama.EmbedAsync(_options.EmbeddingModel, examples.Select(e => e.Text).ToList(), ct);
            return _kindExamples = examples.Zip(vectors)
                .GroupBy(pair => pair.First.Kind)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<float[]>)group.Select(pair => EventClusterer.Normalize(pair.Second)).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "World news kinds fell back to keywords: the examples couldn't be embedded");
            return null;
        }
    }

    // Alerts for people who asked about surging stories (World News > Situations). Only the kinds
    // Overwatch shows by default: a cup final going viral isn't operational news.
    private IReadOnlyList<WorldNewsAlert> CreateSurgeAlerts(DateTimeOffset now)
    {
        var growth = Store.SituationGrowth(now - SituationSurge.Window);
        var surging = Store.TopSituations(now - SituationSurge.Window, MaxEvents)
            .Where(s => s.Kind is not null && SituationKinds.Operational.Contains(s.Kind))
            .Select(s => (Situation: s, Growth: growth.GetValueOrDefault(s.Id)))
            .Where(item => SituationSurge.IsSurging(item.Growth.Outlets, item.Growth.OutletsBefore))
            .Select(item => (item.Situation.Id, item.Situation.Title, SituationSurge.Message(item.Growth.Outlets, item.Growth.OutletsBefore)))
            .ToList();
        return Store.CreateSurgeAlerts(surging, now);
    }

    // SentinelGPT's search_world_news: the last week's events that match the question by meaning,
    // with their situation and a few sources. Falls back to matching headline words when the
    // embedding model can't be reached.
    public async Task<WorldNewsSearchResult> SearchAsync(string query, CancellationToken ct, double minScore = WorldNewsSearch.MinScore)
    {
        var question = query.Trim();
        if (question.Length == 0) return new WorldNewsSearchResult(question, "nothing", []);
        var since = DateTimeOffset.UtcNow - WorldNewsSearch.Window;
        var candidates = await Task.Run(() => Store.SearchCandidates(since, WorldNewsSearch.MinArticles), ct);
        var situations = candidates.ToDictionary(c => c.EventId, c => c.SituationId);

        IReadOnlyList<string> ids;
        string matchedBy;
        try
        {
            var vector = (await _ollama.EmbedAsync(_options.EmbeddingModel, [question], ct))[0];
            ids = WorldNewsSearch.Rank(vector, candidates, minScore: minScore).Select(hit => hit.EventId).ToList();
            matchedBy = "meaning";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "World news search couldn't embed the question; matching headline words instead");
            ids = WorldNewsSearch.MatchWords(question, TopEvents(168));
            matchedBy = "headline words";
        }

        var items = await Task.Run(() => ids.Select(id => Describe(id, situations.GetValueOrDefault(id))).OfType<WorldNewsSearchItem>().ToList(), ct);
        return new WorldNewsSearchResult(question, matchedBy, items);
    }

    private WorldNewsSearchItem? Describe(string eventId, string? situationId)
    {
        if (Store.Event(eventId) is not { } e) return null;
        var summary = Store.LatestSummary(eventId);
        WorldNewsSearchSituation? situation = null;
        if (situationId is not null && Store.EventsForSituation(situationId) is { Count: >= 2 } linked)
        {
            var biggest = linked.OrderByDescending(x => x.Languages.Contains("English")).ThenByDescending(x => x.ArticleCount).First();
            situation = new WorldNewsSearchSituation(biggest.Headline ?? biggest.Title,
                SituationLinker.IsActive(linked.Max(x => x.LastSeen), DateTimeOffset.UtcNow), linked.Count);
        }
        return new WorldNewsSearchItem(e.Headline ?? e.Title, e.Headline is null ? null : e.Title, summary?.Text, e.OutletCount, e.ArticleCount,
            e.Countries, e.Languages, e.FirstSeen, e.LastSeen, situation, WorldNewsSearch.Sources(Store.ArticlesForEvent(eventId)));
    }

    // The event's combined document as Markdown, or null when the event is gone.
    public (string FileName, string Markdown)? Document(string eventId)
    {
        if (Store.Event(eventId) is not { } e) return null;
        var markdown = EventDocument.ToMarkdown(e, Store.ArticlesForEvent(eventId), Store.LatestSummary(eventId), at => at.ToLocalGwsDateTime());
        return ($"world-news-{e.FirstSeen.ToLocalTime():yyyyMMdd}-{eventId[..Math.Min(8, eventId.Length)]}.md", markdown);
    }

    public void Dispose()
    {
        _ollama.Dispose();
        _running.Dispose();
    }
}

// World News as reading for SentinelGPT's web answers: only events the question is clearly about
// (matched by meaning - a headline-word fallback is too loose to add unasked), top five.
public sealed class WorldNewsAssistantSearch(WorldNewsService news) : GwsBusinessSuite.Application.NewsIntelligence.IWorldNewsSearch
{
    public async Task<GwsBusinessSuite.Application.NewsIntelligence.WorldNewsContext?> ContextAsync(string question, CancellationToken cancellationToken = default)
    {
        var result = await news.SearchAsync(question, cancellationToken, WorldNewsSearch.ContextMinScore);
        if (result.MatchedBy != "meaning" || result.Results.Count == 0) return null;
        var text = new System.Text.StringBuilder();
        var sources = new List<GwsBusinessSuite.Application.NewsIntelligence.WorldNewsContextSource>();
        foreach (var item in result.Results.Take(5))
        {
            text.AppendLine($"NEWS EVENT: {item.Headline} - {item.Outlets} outlets; reported from {string.Join(", ", item.Countries.Take(8))}; " +
                $"{item.FirstSeen.ToLocalGwsDateTime()} to {item.LastSeen.ToLocalGwsDateTime()}");
            if (item.Situation is { } situation)
                text.AppendLine($"PART OF: {situation.Title} ({(situation.Active ? "active" : "quiet")}, {situation.Events} events)");
            if (item.Summary is { } summary) text.AppendLine($"SUMMARY (AI-written from headlines): {summary}");
            foreach (var source in item.Sources)
            {
                text.AppendLine($"SOURCE: {source.Outlet} ({source.Language}): {source.Title} - {source.Url}");
                sources.Add(new(source.Outlet + ": " + source.Title, source.Url));
            }
        }
        return new GwsBusinessSuite.Application.NewsIntelligence.WorldNewsContext(text.ToString(), sources);
    }
}

// Reads the news every 15 minutes, starting a minute after the app starts (WorldNews:Enabled
// turns it off; WorldNews:StartupDelaySeconds changes the wait).
public sealed class WorldNewsBackgroundService(WorldNewsService news, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("WorldNews:Enabled", true)) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(configuration.GetValue("WorldNews:StartupDelaySeconds", 60)), stoppingToken);
            using var timer = new PeriodicTimer(WorldNewsService.Interval);
            do
            {
                await news.RefreshAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
