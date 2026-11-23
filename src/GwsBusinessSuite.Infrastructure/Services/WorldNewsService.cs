using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Infrastructure.WebCrawl;
using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// World News (Intelligence workspace, .claude/plans/world-news-situations-macos-2026-10.md):
// WorldNewsKit's pipeline on the server. Google News country editions and outlet-owned RSS
// feeds are read every 15 minutes and grouped into events across languages with embeddinggemma
// on the server's Ollama. embeddinggemma is in ollama/server-models.txt, and Light mode allows
// it, since it's embeddings, not generation. After each read the busiest events get an English
// headline and summary from the same model and service Media Watch summarizes with (summaries
// are what Light mode keeps on the server). The data lives in its own SQLite file beside the
// main database (no migrations), keeping 30 days.
// AnalysisAt: when the analysis it shows was written (for "what changed since you last read").
public sealed record WorldNewsDocument(string Title, string FileName, string Markdown, DateTimeOffset? AnalysisAt = null);

public sealed partial class WorldNewsService : IDisposable
{
    public const string HttpClientName = "world-news";
    // The last finished read is kept in the news database, so a restart doesn't show "Not read yet".
    private const string LastReadKey = "world-news:last-read";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    // How often the background service reads (WorldNews:ReadIntervalMinutes, 5 by default since
    // 18-11-26). Each feed still keeps its own pace (FeedSchedule), so a short interval mostly
    // means a busy outlet's new stories arrive within minutes rather than up to 15.
    public TimeSpan ReadInterval { get; }
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
    // ServerAi None: no Ollama on this server, so anything to embed waits for the Mac's relay.
    private readonly bool _serverHasNoModel;
    // The server's model is the only source (ServerAiOptions.ServerModelOnly): no work for the Mac app.
    private readonly bool _serverModelOnly;

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _model;
    private readonly WorldNewsArticleReader? _articleReader;
    private readonly WebCrawler? _webCrawler;
    private readonly int _maxCrawlPagesPerCycle;

    public WorldNewsService(IConfiguration configuration, IOptions<ContentStudioOptions> ollamaOptions,
        IHttpClientFactory httpClientFactory, IServiceScopeFactory scopes, ILogger<WorldNewsService> logger,
        OllamaWorkloadScheduler? workloads = null, WorldNewsMapCache? mapCache = null, WorldNewsArticleReader? articleReader = null,
        WebCrawler? webCrawler = null)
    {
        _scopes = scopes;
        _workloads = workloads;
        _httpClientFactory = httpClientFactory;
        _articleReader = articleReader;
        _webCrawler = webCrawler;
        _maxCrawlPagesPerCycle = Math.Clamp(configuration.GetValue("WorldNews:MaxCrawlPagesPerCycle", 500), 1, 2000);
        _mapCache = mapCache;
        _logger = logger;
        _serverHasNoModel = configuration.GetValue<ServerAiMode>($"{ServerAiOptions.SectionName}:Mode") == ServerAiMode.None;
        _serverModelOnly = configuration.GetSection(ServerAiOptions.SectionName).Get<ServerAiOptions>()?.ServerModelOnly == true;
        var path = Path.GetFullPath(configuration["WorldNews:DatabasePath"] ?? "/app/data/worldnews.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Store = new WorldNewsStore(path);
        LoadOwners();
        LoadSiteNetworks();
        var baseUrl = string.IsNullOrWhiteSpace(ollamaOptions.Value.BaseUrl) ? ContentStudioOptions.DefaultBaseUrl : ollamaOptions.Value.BaseUrl;
        _ollama = new OllamaClient(new Uri(baseUrl.TrimEnd('/') + "/"), new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        var http = httpClientFactory.CreateClient(HttpClientName);
        // Embedding takes the server's one Ollama slot a batch at a time, behind interactive work, so
        // the two models no longer run at once and saturate every core (seen 30-10-26: Ollama at
        // 371% of the droplet's 400% while the web app got 3.5%).
        _options = new WorldNewsOptions
        {
            MaxEmbedPerCycle = configuration.GetValue("WorldNews:MaxEmbedPerCycle", 1500),
            // WHO's own outbreak reports alongside the outlets' (07-11-26).
            ReadWhoOutbreakNews = configuration.GetValue("WorldNews:ReadWhoOutbreakNews", true),
            // Wikipedia's Current events portal as an early signal (World News trust plan 1.19).
            ReadWikipediaCurrentEvents = configuration.GetValue("WorldNews:ReadWikipediaCurrentEvents", true),
            EmbedElsewhere = () => EmbedRelayActive(DateTimeOffset.UtcNow),
            FollowUpQueries = FollowUpSearches,
            AddedOutlets = () => Store.CustomFeeds(),
            PausedFeeds = () => Store.PausedFeeds(),
            EmbedSlot = workloads is null ? null : ct => workloads.AcquireAsync(OllamaWorkloadPriority.Background, ct).AsTask(),
            // Outlet feeds are read every few minutes now (ReadInterval); Google News and GDELT keep their 15.
            SlowSourceGap = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("WorldNews:SlowSourceMinutes", 15), 0, 120)),
            Grouping = configuration.GetValue("WorldNews:Grouping:Enabled", true)
                ? new GroupingRules(
                    PlaceGuard: configuration.GetValue("WorldNews:Grouping:PlaceGuard", true),
                    QuietPenalty: Math.Clamp(configuration.GetValue("WorldNews:Grouping:QuietPenalty", 0.05), 0, 0.2))
                : null
        };
        ReadInterval = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("WorldNews:ReadIntervalMinutes", 5), 2, 60));
        PullCooldown = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("WorldNews:ManualPullCooldownMinutes", 5), 1, 60));
        _regroupAiChecks = Math.Clamp(configuration.GetValue("WorldNews:Regroup:AiChecks", 6), 0, 30);
        // One GDELT client for the reads and for people's searches, so both share its pacing.
        _gdelt = new GdeltDocClient(http, TimeProvider.System);
        _pipeline = new WorldNewsPipeline(Store, http, _gdelt, _ollama, TimeProvider.System, _options);
        // IOllamaService is a typed HttpClient, so each summary takes a fresh one rather than this
        // singleton holding one for the app's lifetime. 8k context fits SummaryOptions.MaxPromptChars.
        var model = _model = ollamaOptions.Value.Model;
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
            new SummaryOptions
            {
                // Every story two or more outlets carry gets a summary (20-11-26): with the model on
                // the server, four a read (every 15 minutes) keeps up; a one-outlet story waits.
                PerRun = configuration.GetValue("WorldNews:SummariesPerRead", 4),
                MinOutlets = configuration.GetValue("WorldNews:SummaryMinOutlets", 2),
                Window = TimeSpan.FromDays(30)
            });
        LastRefreshed = Store.LastFetched(LastReadKey);
        if (LastRefreshed is not null) Status = string.Empty;
        void RebuildFailed(Exception ex) => _logger.LogWarning(ex, "Rebuilding a World News list failed");
        _eventLists = new EventListCache(since => Timed("event list", since, () => Store.TopEvents(since, MaxEvents)),
            TimeProvider.System, Interval, onError: RebuildFailed);
        _situationLists = new RangeListCache<SituationOverview>(since => Timed("situation list", since, () => Store.TopSituations(since, MaxEvents)),
            TimeProvider.System, Interval, onError: RebuildFailed);
        _worldSnapshots = new RangeListCache<WorldSnapshot>(since => Timed("world view", since, () => (IReadOnlyList<WorldSnapshot>)[Store.World(since)]),
            TimeProvider.System, Interval, onError: RebuildFailed);
        // Built at startup, so the first visit after a deploy doesn't wait for it.
        WarmEventLists();
    }

    // The busiest events in the last rangeHours, from memory unless a read has changed them.
    public IReadOnlyList<EventOverview> TopEvents(int rangeHours) => _eventLists.Get(rangeHours);

    // Developing stories with news in the last rangeHours, likewise kept in memory between reads.
    public IReadOnlyList<SituationOverview> TopSituations(int rangeHours) => _situationLists.Get(rangeHours);

    public WorldSnapshot World(int rangeHours) => _worldSnapshots.Get(rangeHours)[0];

    // The list first, so the page has it soon after a deploy; then the upkeep a database from
    // before 30-10-26 needs, once (each step does nothing after that): the covering index, dropping
    // grouped reports' embeddings, and giving that space back. Minutes over the droplet's 2 GB.
    // Each list's build time in the log (30-10-26): it took 50-70 s before the covering index.
    private IReadOnlyList<T> Timed<T>(string list, DateTimeOffset since, Func<IReadOnlyList<T>> build)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var built = build();
        _logger.LogInformation("World News {List} for the last {Hours:N0} h built in {Ms:N0} ms",
            list, (DateTimeOffset.UtcNow - since).TotalHours, started.ElapsedMilliseconds);
        return built;
    }

    private const int CompactAfterReleasing = 10_000;

    private void WarmEventLists() => _ = Task.Run(async () =>
    {
        try { _eventLists.Get(24); }
        catch (Exception ex) { _logger.LogWarning(ex, "Building the world news event list failed"); }
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var indexed = Store.EnsureCoveringIndex();
            // The full-text index too (World News trust plan 1.10), off the startup path.
            if (Store.EnsureFullTextIndex()) _logger.LogInformation("World News: full-text index built.");
            var released = Store.ReleaseGroupedEmbeddings(betweenBatches: () => Thread.Sleep(50))
                + Store.ReleaseStaleVectors(DateTimeOffset.UtcNow, betweenBatches: () => Thread.Sleep(50));
            // Compacting holds off writers for minutes, so it takes the read's turn: a read due
            // meanwhile is skipped rather than failing on a locked database.
            var compacted = released >= CompactAfterReleasing;
            if (compacted)
            {
                await _running.WaitAsync();
                try { Store.Compact(); }
                finally { _running.Release(); }
            }
            if (!indexed && released == 0 && !compacted) return;
            _logger.LogInformation(
                "World News upkeep done in {Seconds:N0} s: covering index {Indexed}, {Released:N0} grouped reports' embeddings dropped, compacted {Compacted}",
                started.Elapsed.TotalSeconds, indexed, released, compacted);
            ClearLists();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "World News upkeep failed; it's tried again at the next start"); }
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
    public bool LastReadFailed { get; private set; }

    // The page says plainly when the news isn't updating (19-11-26): every read failed for hours and
    // the header still read "Updated <time>", with the error only in a tooltip.
    public bool IsBehind(DateTimeOffset now) => IsBehind(LastReadFailed, LastRefreshed, ReadInterval, now);

    public static bool IsBehind(bool lastReadFailed, DateTimeOffset? lastRead, TimeSpan readInterval, DateTimeOffset now) =>
        lastReadFailed || (lastRead is { } at && now - at > readInterval * 3);
    public event Action? Changed;
    public event Action<string, WorldNewsAlert>? AlertCreated;

    // One read. A read already running is left to finish rather than doubled up.
    public Task RefreshAsync(CancellationToken ct) => ReadAsync(null, wait: false, ct);

    // wait: a person's Pull latest waits for a running read to finish, then reads again with its
    // own request. Null when no read ran or it failed.
    private async Task<CycleReport?> ReadAsync(ReadRequest? request, bool wait, CancellationToken ct)
    {
        if (wait) await _running.WaitAsync(ct);
        else if (!await _running.WaitAsync(0, ct)) return null;
        CycleReport? finished = null;
        try
        {
            Status = request?.Force == true ? "Pulling the latest news now..." : "Reading the world's news...";
            Changed?.Invoke();
            // Synchronous SQLite work and CPU-bound grouping: keep it off the caller's thread.
            var crawlerImport = new WorldNewsImportResult(0, 0, 0);
            var crawlerAttempted = 0;
            var crawlerSucceeded = 0;
            var crawlerNotes = new List<string>();
            if (_webCrawler is not null)
            {
                crawlerAttempted = 1;
                try
                {
                    crawlerImport = await _webCrawler.ImportWorldNewsPagesAsync(Store, _maxCrawlPagesPerCycle, ct);
                    crawlerSucceeded = 1;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    crawlerNotes.Add($"Media Watch crawler import: {ex.Message}");
                }
            }
            var report = await Task.Run(() => _pipeline.RunCycleAsync(request, ct), ct);
            if (crawlerAttempted > 0)
            {
                report = report with
                {
                    Fetched = report.Fetched + crawlerImport.Eligible,
                    Added = report.Added + crawlerImport.Added,
                    SourcesAttempted = report.SourcesAttempted + crawlerAttempted,
                    SourcesSucceeded = report.SourcesSucceeded + crawlerSucceeded,
                    Notes = report.Notes.Concat(crawlerNotes).ToList()
                };
            }
            await ClassifySituationsAsync(ct);
            await RefineSubtypesAsync(ct);
            var alerts = Store.CreateWatchAlerts(DateTimeOffset.UtcNow, MayAlert).Concat(CreateSurgeAlerts(DateTimeOffset.UtcNow))
                .Concat(Store.CreateKeywordAlerts(DateTimeOffset.UtcNow, MayAlert))
                // N5: new stories on people's beats (the last day's), gated like every other alert.
                .Concat(Store.CreateBeatAlerts(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), MayAlert));
            foreach (var alert in alerts) AlertCreated?.Invoke(alert.Username, alert);
            // Outlets' owners from Wikidata, weekly (A3); quietly skipped when Wikidata can't be read.
            await RefreshOwnershipAsync(cancellationToken: ct);
            // Site networks from shared tracking IDs, and a few new domains' registration dates (B5).
            await RefreshSiteNetworksAsync(ct);
            // Labels into the tamper-evident ledger, at most hourly (World News trust plan A7/A8).
            try { SnapshotLabels(DateTimeOffset.UtcNow); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { _logger.LogWarning(ex, "World News: label snapshot failed"); }
            // Stories merged wrongly before the place guard caught them are split, hourly (RecentMergeCheck).
            try { RecheckRecentMerges(DateTimeOffset.UtcNow); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { _logger.LogWarning(ex, "World News: merge re-check failed"); }
            // And the reverse: one event split into two stories is joined, hourly, in the background.
            StartHourlyMergeCheck(DateTimeOffset.UtcNow);
            // Morning briefings and watchlist dossiers in each person's Sentinel, hourly (20-11-26).
            try { await WritePersonalPagesIfDueAsync(DateTimeOffset.UtcNow, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "World News: personal pages failed"); }
            LastRefreshed = DateTimeOffset.UtcNow;
            LastReadFailed = false;
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
            var latestReport = report.LatestReportAt is { } latest ? $" Latest report {latest.ToLocalGwsDateTime()}." : string.Empty;
            var crawlerSummary = crawlerSucceeded == 0 ? string.Empty
                : $" Crawler scanned {crawlerImport.Scanned:N0} pages; {crawlerImport.Eligible:N0} eligible.";
            Status = $"{report.Added:N0} new of {report.Fetched:N0} reports ({report.Deduplicated:N0} already stored); "
                + $"{report.SourcesSucceeded}/{report.SourcesAttempted} sources succeeded; "
                + $"{report.Embedded:N0} embedded, {report.EmbeddingBacklog:N0} awaiting embedding; "
                + $"{report.NewEvents:N0} new events, {summaries.Written:N0} summaries written.{latestReport}{crawlerSummary}{problems}";
            if (notes.Count > 0)
                _logger.LogWarning("World news read finished with {Count} problem(s): {Notes}", notes.Count, string.Join(" | ", notes));
            finished = report;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status = "Reading stopped.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "World news read failed");
            LastReadFailed = true;
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
        return finished;
    }

    private const string RelayKey = "world-news:summary-relay";
    private const string EmbedRelayKey = "world-news:embed-relay";
    // The Mac asks every 3 minutes; this long without it and the server embeds again, so grouping
    // never stalls for long when the app is closed or its model stops answering.
    public static readonly TimeSpan EmbedRelayFreshness = TimeSpan.FromMinutes(10);
    public const int MaxEmbedJobs = 128;

    // Headlines waiting for an embedding, for the Mac app (30-10-26): embedding ~15,000 a day was
    // the droplet's steadiest Ollama load. Only a Mac with the server's own embedding model gets
    // any (vectors from two models can't be compared), and asking is how the server knows to stand
    // aside. macModels: what the Mac has installed.
    public (string Model, IReadOnlyList<WorldArticle> Jobs) EmbedJobsForRelay(IReadOnlyCollection<string> macModels, int limit)
    {
        if (_serverModelOnly || InstalledName(macModels, _options.EmbeddingModel) is null) return (_options.EmbeddingModel, []);
        var now = DateTimeOffset.UtcNow;
        Store.SetLastFetched(EmbedRelayKey, now);
        return (_options.EmbeddingModel, Store.RecentArticlesToEmbed(Math.Clamp(limit, 1, MaxEmbedJobs), now - _options.EventWindow));
    }

    // The kinds' example headlines while they have no vectors (05-11-26). With no model on the
    // server, kinds had fallen back to keywords since 31-10-26, so the Mac that embeds headlines
    // embeds these too, ahead of them. Ids are "kind:<n>". All 45 fit in one of the Mac's batches.
    public const string KindJobPrefix = "kind:";

    public IReadOnlyList<(string Id, string Text)> KindExampleJobsForRelay(IReadOnlyCollection<string> macModels) =>
        _serverModelOnly || _kindExamples is not null || InstalledName(macModels, _options.EmbeddingModel) is null
            ? []
            : KindExampleTexts().Select((example, i) => ($"{KindJobPrefix}{i}", example.Text)).ToList();

    private static List<(string Kind, string Text)> KindExampleTexts() =>
        SituationKinds.Examples.SelectMany(pair => pair.Value.Select(text => (Kind: pair.Key, Text: text))).ToList();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, float[]> _relayKindVectors = new();

    // Once every example has its vector, kinds are classified by meaning again.
    private void SaveKindExampleVectors(IEnumerable<(string Id, float[] Vector)> vectors)
    {
        foreach (var (id, vector) in vectors)
            if (int.TryParse(id.AsSpan(KindJobPrefix.Length), out var index)) _relayKindVectors[index] = vector;
        var examples = KindExampleTexts();
        if (_kindExamples is not null || Enumerable.Range(0, examples.Count).Any(i => !_relayKindVectors.ContainsKey(i))) return;
        _kindExamples = BuildKindExamples(examples, examples.Select((_, i) => _relayKindVectors[i]).ToList());
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<float[]>> BuildKindExamples(
        List<(string Kind, string Text)> examples, IReadOnlyList<float[]> vectors) =>
        examples.Zip(vectors)
            .GroupBy(pair => pair.First.Kind)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<float[]>)group.Select(pair => EventClusterer.Normalize(pair.Second)).ToList());

    // The Mac's embeddings: saved for the reports still waiting, then grouped straight away unless
    // a read is running (it groups them itself). An error message, or null.
    public string? SaveRelayEmbeddings(string model, IReadOnlyList<(string Id, float[] Vector)> embeddings)
    {
        if (InstalledName([model], _options.EmbeddingModel) is null)
            return $"The server groups headlines with {_options.EmbeddingModel}, not {model}.";
        var dimensions = Store.EmbeddingDimensions() ?? embeddings.FirstOrDefault().Vector?.Length ?? 0;
        if (embeddings.Any(e => e.Vector is null || e.Vector.Length != dimensions || e.Vector.Any(v => !float.IsFinite(v))))
            return $"Each embedding needs {dimensions} numbers.";
        var kinds = embeddings.Where(e => e.Id.StartsWith(KindJobPrefix, StringComparison.Ordinal)).ToList();
        if (kinds.Count > 0)
        {
            SaveKindExampleVectors(kinds);
            embeddings = embeddings.Where(e => !e.Id.StartsWith(KindJobPrefix, StringComparison.Ordinal)).ToList();
            if (embeddings.Count == 0) return null;
        }
        if (Store.FillEmbeddings(embeddings) == 0 || !_running.Wait(0)) return null;
        try
        {
            _pipeline.Group();
        }
        finally
        {
            _running.Release();
        }
        ClearLists();
        _mapCache?.Clear();
        Changed?.Invoke();
        return null;
    }

    public bool EmbedRelayActive(DateTimeOffset now) => !_serverModelOnly && Store.LastFetched(EmbedRelayKey) is { } at && at >= now - EmbedRelayFreshness;

    // "embeddinggemma" is installed as "embeddinggemma" or "embeddinggemma:latest"; a tagged model
    // has to match its tag.
    private static string? InstalledName(IEnumerable<string> installed, string model) =>
        installed.FirstOrDefault(name => name.Equals(model, StringComparison.OrdinalIgnoreCase)
            || (!model.Contains(':') && name.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase)));
    // How recently the Mac app must have asked for work for the server's model to stand aside.
    public static readonly TimeSpan RelayFreshness = TimeSpan.FromHours(2);

    // The Mac app (signed in as an admin) asks for events to summarize with its local model.
    // Asking is also how the server knows the Mac is doing it.
    public IReadOnlyList<SummaryJob> SummaryJobsForRelay(int limit)
    {
        if (_serverModelOnly) return [];
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

    public bool RelayActive(DateTimeOffset now) => !_serverModelOnly && Store.LastFetched(RelayKey) is { } at && at >= now - RelayFreshness;

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

    // SentinelGPT tags a few of the biggest stories the keywords couldn't (SubtypeRefinement), each
    // once, at background priority on the Mac first. No model answering just leaves them for later.
    private async Task RefineSubtypesAsync(CancellationToken ct)
    {
        if (_serverHasNoModel) return;
        var since = DateTimeOffset.UtcNow - SituationLinker.DefaultWindow;
        var asked = 0;
        foreach (var situation in Store.TopSituations(since, 60))
        {
            if (asked >= SubtypeRefinement.MaxPerRead || ct.IsCancellationRequested) break;
            var kind = WorldNewsMapService.KindOf(situation);
            if (SituationSubtypes.Choices(kind).Count == 0 || Store.ModelSubtype(situation.Id) is not null) continue;
            if (SituationSubtypes.Tag(kind, situation.Title, situation.Latest).Confidence != SituationTag.Low) continue;
            var headlines = Store.EventsForSituation(situation.Id).OrderByDescending(e => e.LastSeen).Take(3)
                .SelectMany(e => Store.ArticlesForEvent(e.Id)).Select(a => a.English?.Headline ?? a.Title)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
            if (headlines.Count == 0) continue;
            asked++;
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromMinutes(2));
                var answer = await scope.ServiceProvider.GetRequiredService<IOllamaService>()
                    .GenerateAsync(_model, SubtypeRefinement.SystemPrompt, SubtypeRefinement.Prompt(kind, headlines), limit.Token);
                if (SubtypeRefinement.Parse(kind, answer) is { } subtype) Store.SaveModelSubtype(situation.Id, subtype, DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "World News: SentinelGPT couldn't tag {Situation}; it's asked again on a later read", situation.Id);
                break;
            }
        }
    }

    // The kinds' example headlines as unit vectors, embedded once per run of the app.
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<float[]>>?> KindExamplesAsync(CancellationToken ct)
    {
        if (_kindExamples is not null) return _kindExamples;
        // The Mac embeds them alongside headlines (KindExampleJobsForRelay); asking a server with no
        // model only fails, which logged a warning every read (~97 a day after 31-10-26).
        if (_serverHasNoModel || EmbedRelayActive(DateTimeOffset.UtcNow)) return null;
        try
        {
            var examples = KindExampleTexts();
            var vectors = await _ollama.EmbedAsync(_options.EmbeddingModel, examples.Select(e => e.Text).ToList(), ct);
            return _kindExamples = BuildKindExamples(examples, vectors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "World news kinds fell back to keywords: the examples couldn't be embedded");
            return null;
        }
    }

    // Alerts for people who asked about surging stories (World News > Situations). Only the kinds
    // Overwatch shows by default: a cup final going viral isn't operational news.
    // Surging stories' titles from the last read, searched for on the next one (trust plan 1.14).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _surgingTitles = new();

    private IReadOnlyList<string> FollowUpSearches() =>
        _surgingTitles.Values.Select(FollowUpQueries.From).OfType<string>().Take(FollowUpQueries.MaxPerRead).ToList();

    private IReadOnlyList<WorldNewsAlert> CreateSurgeAlerts(DateTimeOffset now)
    {
        _surgingTitles.Clear();
        var growth = Store.SituationGrowth(now - SituationSurge.Window);
        var surging = Store.TopSituations(now - SituationSurge.Window, MaxEvents)
            .Where(s => s.Kind is not null && SituationKinds.Operational.Contains(s.Kind))
            .Select(s => (Situation: s, Growth: growth.GetValueOrDefault(s.Id)))
            .Where(item => SituationSurge.IsSurging(item.Growth.Outlets, item.Growth.OutletsBefore))
            .Where(item => { _surgingTitles[item.Situation.Id] = item.Situation.Title; return true; })
            .Where(item => MayAlert(item.Situation.Id))
            .Select(item => (item.Situation.Id, item.Situation.Title, SituationSurge.Message(item.Growth.Outlets, item.Growth.OutletsBefore)))
            .ToList();
        return Store.CreateSurgeAlerts(surging, now);
    }

    // The breaking-news gate (World News trust plan 1.4, 13-11-26): nobody is alerted to a story that
    // is still one source, however many outlets repeat it. It alerts on a later read once a second
    // independent source has it. Only the situation's latest events are read, so a long-running
    // story costs a bounded number of rows.
    private bool MayAlert(string situationId)
    {
        // N11: a story on hold sends nothing until its release time.
        if (Store.IsSituationHeld(situationId, DateTimeOffset.UtcNow)) return false;
        var articles = Store.EventsForSituation(situationId).OrderByDescending(e => e.LastSeen).Take(5)
            .SelectMany(e => Store.ArticlesForEvent(e.Id)).ToList();
        return StoryConfidenceRules.MayAlert(SourceIndependence.Assess(articles));
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

    // World News search (13-11-26): stories with similar reports, by meaning and by words, merged.
    // kind narrows to one kind of story (e.g. cyber). Meaning needs the embedding model; without it
    // the words alone answer, and the note says so.
    public async Task<FindResult> FindAsync(string query, int days, string? kind = null, CancellationToken ct = default)
    {
        var text = query.Trim();
        if (text.Length == 0) return new FindResult(text, [], null);
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-Math.Clamp(days, 1, 30));
        var meaningSince = since > now - WorldNewsFinder.MeaningWindow ? since : now - WorldNewsFinder.MeaningWindow;
        IReadOnlyList<(string, double)> meaning = [];
        string? note = null;
        try
        {
            var candidates = await Task.Run(() => Store.SearchCandidates(meaningSince, 1).Where(c => c.Centroid.Length > 0).ToList(), ct);
            var vector = (await _ollama.EmbedAsync(_options.EmbeddingModel, [text], ct))[0];
            meaning = WorldNewsSearch.Rank(vector, candidates, limit: WorldNewsFinder.Limit, minScore: WorldNewsFinder.MinSimilarity, perSituation: 4)
                .Select(hit => (hit.EventId, hit.Score)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "World News search: meaning unavailable, words only");
            note = "Matched by words only just now: the model that compares meaning didn't answer.";
        }
        var words = await Task.Run(() => Store.EventsMatchingText(WorldNewsFinder.WordQuery(text), since, WorldNewsFinder.Limit * 2), ct);
        var stories = await Task.Run(() => Found(WorldNewsFinder.Merge(meaning, words, WorldNewsFinder.Limit * 2), kind), ct);
        if (days > 7 && note is null) note = "Older than a week, stories are found by their words.";
        return new FindResult(text, stories.Take(WorldNewsFinder.Limit).ToList(), note);
    }

    // "Open in World News" from an Overwatch window (19-11-26): the stories about it, close matches
    // first, each with its latest summary.
    public async Task<IReadOnlyList<BriefCoverage>> BriefCoverageAsync(OverwatchReport report, CancellationToken ct = default)
    {
        var found = await FindAsync(report.Query, report.Days, ct: ct);
        var ordered = OverwatchBrief.Order(found.Stories);
        return await Task.Run(() => ordered.Select(s => new BriefCoverage(s, Store.LatestSummary(s.Event.Id)?.Text)).ToList(), ct);
    }

    // SentinelGPT's executive summary of the report and what the news says about it. Null when the
    // model gave nothing; it throws when no model answered, for the page to say so.
    public async Task<string?> BriefSummaryAsync(OverwatchReport report, IReadOnlyList<BriefCoverage> coverage, CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        var text = await scope.ServiceProvider.GetRequiredService<IOllamaService>()
            .GenerateAsync(_model, OverwatchBrief.SystemPrompt, OverwatchBrief.Prompt(report, coverage), limit.Token);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // Stories like this one (its own vector against the last week's), most similar first.
    public async Task<FindResult> SimilarToAsync(string eventId, CancellationToken ct = default)
    {
        var story = await Task.Run(() => Store.Event(eventId), ct);
        if (story is null) return new FindResult(string.Empty, [], "That story is no longer kept.");
        var title = story.Headline ?? story.Title;
        var vector = await Task.Run(() => Store.EventVector(eventId), ct);
        if (vector is null) return await FindAsync(title, 30);
        var candidates = await Task.Run(() => Store.SearchCandidates(DateTimeOffset.UtcNow - WorldNewsFinder.MeaningWindow, 1)
            .Where(c => c.EventId != eventId && c.Centroid.Length == vector.Length).ToList(), ct);
        var hits = WorldNewsSearch.Rank(vector, candidates, limit: WorldNewsFinder.Limit, minScore: WorldNewsFinder.SimilarToStory, perSituation: 4)
            .Select(hit => (hit.EventId, hit.Score)).ToList();
        var stories = await Task.Run(() => Found(WorldNewsFinder.Merge(hits, [], WorldNewsFinder.Limit), null), ct);
        return new FindResult(title, stories, stories.Count == 0 ? "Nothing else in the last week reads like this story." : null);
    }

    // Cyber attacks and breaches in the news now, each with its status and the CVEs it names.
    // Classified cyber stories, and any recent story whose headline is plainly about an attack.
    private (DateTimeOffset At, int Hours, IReadOnlyList<FoundStory> Stories)? _cyberCache;
    private readonly GdeltDocClient _gdelt;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, WorldwideResult Result)> _worldwideCache = new(StringComparer.OrdinalIgnoreCase);

    // Wider search (13-11-26): the same words across every outlet GDELT reads, in 65 languages, as
    // far back as chosen (three months at most). One request per search through the shared paced
    // gate; the same search within ten minutes is answered from memory. Reports World News already
    // holds are linked to their story.
    public async Task<WorldwideResult> WorldwideAsync(string query, int days, CancellationToken ct = default)
    {
        var text = WorldNewsFinder.GdeltQuery(query);
        if (text is null) return new WorldwideResult([], "Use words of three letters or more for the worldwide search.");
        var key = $"{days}|{text}";
        if (_worldwideCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10)) return cached.Result;
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90));
        var result = await _gdelt.SearchAsync(text, since, ct, GdeltDocClient.SearchLookback, "hybridrel", 75);
        var known = await Task.Run(() => Store.EventsForArticles(result.Articles.Select(a => a.Id)), ct);
        var worldwide = new WorldwideResult(
            result.Articles.Select(a => new WorldwideReport(a, known.GetValueOrDefault(a.Id))).ToList(), result.Note);
        if (result.Note is null)
        {
            _worldwideCache[key] = (DateTimeOffset.UtcNow, worldwide);
            foreach (var old in _worldwideCache.Where(pair => DateTimeOffset.UtcNow - pair.Value.At > TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToList())
                _worldwideCache.TryRemove(old, out _);
        }
        return worldwide;
    }

    public async Task<IReadOnlyList<FoundStory>> CyberInActionAsync(int hours = 72, CancellationToken ct = default)
    {
        // Read at most every five minutes: it scans the period's headlines.
        if (_cyberCache is { } cached && cached.Hours == hours && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(5)) return cached.Stories;
        var since = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours, 1, 720));
        var stories = await Task.Run(() =>
        {
            var classified = Store.EventsOfKind(SituationKinds.Cyber, since, 60);
            var byHeadline = Store.TopEvents(since, 3000).Where(e => WorldNewsFinder.IsCyberHeadline(e.Headline) || WorldNewsFinder.IsCyberHeadline(e.Title));
            var events = classified.Concat(byHeadline).DistinctBy(e => e.Id)
                .OrderByDescending(e => e.OutletCount).ThenByDescending(e => e.LastSeen).Take(60).ToList();
            return Found(events.Select(e => (e.Id, (double?)null, 0)).ToList(), null);
        }, ct);
        var cyber = stories.Where(story => story.IsCyber).ToList();
        _cyberCache = (DateTimeOffset.UtcNow, hours, cyber);
        return cyber;
    }

    private List<FoundStory> Found(IReadOnlyList<(string EventId, double? Similarity, int Reports)> hits, string? kind)
    {
        var kinds = Store.EventKinds(hits.Select(hit => hit.EventId));
        var found = new List<FoundStory>();
        foreach (var (id, similarity, reports) in hits)
        {
            var storyKind = kinds.GetValueOrDefault(id);
            if (Store.Event(id) is not { } e) continue;
            var articles = Store.ArticlesForEvent(id);
            var texts = articles.SelectMany(a => new[] { a.Title, a.Snippet }).Append(e.Headline).ToList();
            var story = new FoundStory(e, similarity, reports, storyKind,
                WorldNewsFinder.CyberStatusOf(storyKind, articles.Select(a => a.Title).Append(e.Headline)),
                WorldNewsFinder.Cves(texts));
            // "Cyber only" keeps cyber stories, classified or plainly so from their headlines.
            if (kind == SituationKinds.Cyber ? !story.IsCyber : kind is { Length: > 0 } && storyKind != kind) continue;
            found.Add(story);
        }
        return found;
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

    // The event's combined document as Markdown (shown rendered on the page), or null when the
    // event is gone. FileName is the PDF's (DocumentPdf).
    // With a username, the document starts with what changed since that reader last read it.
    public WorldNewsDocument? Document(string eventId, string? username = null)
    {
        if (Story(eventId, username) is not { } story) return null;
        var e = story.Event;
        var markdown = EventDocument.ToMarkdown(story, at => at.ToLocalGwsDateTime());
        var title = e.Headline ?? e.Title;
        var slug = string.Join('-', NonSlug().Split(title.ToLowerInvariant()).Where(part => part.Length > 0));
        var name = slug.Length == 0 ? eventId[..Math.Min(8, eventId.Length)] : slug[..Math.Min(slug.Length, 60)].TrimEnd('-');
        return new WorldNewsDocument(title, $"world-news-{e.FirstSeen.ToLocalTime():yyyyMMdd}-{name}.pdf", markdown, story.Analysis?.CreatedAt);
    }

    // The same document as a PDF, made from its Markdown (06-11-26: downloads are PDFs, not Markdown).
    public (string FileName, byte[] Pdf)? DocumentPdf(string eventId) =>
        Document(eventId) is { } document
            ? (document.FileName, MarkdownPdf.Render(document.Markdown, $"World News · made {DateTimeOffset.UtcNow.ToLocalGwsDateTime()}"))
            : null;

    // The story as its document shows it, or null when the event is gone. With a username, it says
    // what changed since that reader last read it (the analysis they saw then against this one).
    public StoryDocument? Story(string eventId, string? username)
    {
        if (Store.Event(eventId) is not { } e) return null;
        var articles = Store.ArticlesForEvent(eventId);
        var analysis = Store.LatestAnalysis(eventId);
        StoryChanges? changes = null;
        if (username is not null && analysis is not null && Store.StoryRead(username, eventId) is { } read
            && read.AnalysisAt.ToUnixTimeSeconds() < analysis.CreatedAt.ToUnixTimeSeconds())
            changes = StoryChangesFinder.Find(analysis, Store.AnalysisAt(eventId, read.AnalysisAt), read.ReadAt, articles);
        return EventDocument.Build(e, articles, Store.LatestSummary(eventId), analysis, Store.TranslationsForEvent(eventId),
            Store.ReadInFull(eventId), changes, Store.Edits(articles.Select(article => article.Id).ToList()));
    }

    // Confirmed by a primary source (World News trust plan 1.13, 13-11-26): WHO's own notice among the
    // reports, or a USGS/GDACS/NHC/NASA record of the same kind of event near the story's place within
    // two days of its first report. Asked after the story shows, so a slow hazard feed never holds it up.
    public async Task<StoryDocument> ConfirmWithPrimarySourcesAsync(StoryDocument story, CancellationToken cancellationToken = default)
    {
        if (story.Trust is not { } trust || trust.PrimarySource is not null) return story;
        var headline = story.Event.Headline ?? story.Event.Title;
        var primary = PrimarySources.InReports(story.Sources.Select(source => source.Report));
        if (primary is null && PrimarySources.KindOf(headline) is not null && WorldNewsPlaces.Instance.Locate(headline) is { } place)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var hazards = scope.ServiceProvider.GetRequiredService<GwsBusinessSuite.Application.Hazards.HazardLayerService>();
                var box = new GwsBusinessSuite.Application.CameraIntel.BoundingBox(
                    Math.Min(85, place.Latitude + 14), Math.Max(-85, place.Latitude - 14), Math.Min(180, place.Longitude + 14), Math.Max(-180, place.Longitude - 14));
                var result = await hazards.GetHazardsAsync(box, cancellationToken);
                var match = PrimarySources.Match(headline, place.Latitude, place.Longitude, story.Event.FirstSeen,
                    result.Features.Select(f => new PrimarySignal(SourceName(f.Url), f.Kind, f.Title, f.Latitude, f.Longitude, f.ObservedAt, f.Url)),
                    place.Precision == WorldNewsPrecision.City ? PrimarySources.CityKilometres : PrimarySources.CountryKilometres);
                if (match is not null) primary = $"{match.Source} ({match.Title})";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(ex, "World News: couldn't check hazards for a story");
            }
        }
        if (primary is null) return story;
        var confidence = StoryConfidenceRules.Assess(trust.Independence, primary, story.Figures.Count(figure => figure.Disputed), trust.FactCheckVerdict,
            story.Event.FirstSeen, DateTimeOffset.UtcNow);
        return story with { Trust = trust with { PrimarySource = primary, Confidence = confidence } };
    }

    private static string SourceName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host switch
        {
            var h when h.EndsWith("usgs.gov", StringComparison.OrdinalIgnoreCase) => "USGS",
            var h when h.Contains("gdacs", StringComparison.OrdinalIgnoreCase) => "GDACS",
            var h when h.EndsWith("nhc.noaa.gov", StringComparison.OrdinalIgnoreCase) => "NOAA National Hurricane Center",
            var h when h.Contains("nasa.gov", StringComparison.OrdinalIgnoreCase) => "NASA",
            var h when h.Contains("nifc", StringComparison.OrdinalIgnoreCase) || h.Contains("arcgis", StringComparison.OrdinalIgnoreCase) => "NIFC",
            var h when h.Contains("ioda", StringComparison.OrdinalIgnoreCase) => "IODA",
            var h when h.EndsWith("faa.gov", StringComparison.OrdinalIgnoreCase) => "FAA",
            var h when h.Contains("firms", StringComparison.OrdinalIgnoreCase) || h.Contains("earthdata", StringComparison.OrdinalIgnoreCase) => "NASA FIRMS",
            var h => h,
        } : "an official record";

    // The sources page (World News trust plan 1.15-1.18, 13-11-26): every feed with its health, the
    // crawl's numbers, coverage by region, outlets' records and countries gone quiet.
    public CrawlDashboard Dashboard(DateTimeOffset now)
    {
        var health = Store.FeedHealth();
        var paused = Store.PausedFeeds();
        var added = Store.CustomFeeds();
        var addedUrls = added.Select(feed => feed.Url).ToHashSet(StringComparer.Ordinal);
        var feeds = OutletFeeds.Outlets.Concat(added).DistinctBy(feed => feed.Url)
            .Select(feed => new CrawlFeed(feed, health.GetValueOrDefault(feed.Url), paused.Contains(feed.Url), addedUrls.Contains(feed.Url)))
            .OrderBy(feed => feed.Outlet.Name, StringComparer.OrdinalIgnoreCase).ThenBy(feed => feed.Outlet.Language)
            .ToList();
        var hours = Store.CountryHours(now.AddDays(-7));
        var regions = feeds.GroupBy(feed => WorldRegions.Of(feed.Outlet.Country))
            .Select(region => new CrawlRegion(region.Key,
                region.Select(feed => feed.Outlet.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                hours.Where(row => WorldRegions.Of(row.Country) == region.Key).Sum(row => row.Reports)))
            .OrderBy(region => region.Reports)
            .ToList();
        return new CrawlDashboard(
            hours.Where(row => row.Hour >= now.AddHours(-1)).Sum(row => row.Reports),
            hours.Where(row => row.Hour >= now.AddDays(-1)).Sum(row => row.Reports),
            Store.CountNewEvents(now.AddDays(-1)),
            feeds,
            regions,
            Store.OutletRecords(now.AddDays(-7)),
            CoverageSilenceRules.Find(hours, now));
    }

    // Feeds a homepage offers that really return stories, for the sources page's "Add an outlet".
    // A site that refuses the app is reported as refusing; nothing is done to get round it.
    public async Task<(IReadOnlyList<FeedCandidate> Feeds, string? Problem)> DiscoverFeedsAsync(string homepage, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(homepage.Trim(), UriKind.Absolute, out var page) || page.Scheme is not ("https" or "http"))
            return ([], "Enter the outlet's homepage, starting with https://");
        var http = _httpClientFactory.CreateClient(HttpClientName);
        string html;
        try
        {
            using var response = await http.GetAsync(page, cancellationToken);
            if (FeedSchedule.IsRefusal((int)response.StatusCode))
                return ([], $"{page.Host} refuses automated requests (HTTP {(int)response.StatusCode}), so it can't be added.");
            html = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ([], $"{page.Host} didn't answer: {ex.Message}");
        }
        var found = new List<FeedCandidate>();
        foreach (var url in FeedDiscovery.Candidates(html, page).Take(12))
        {
            try
            {
                using var response = await http.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var copy = new MemoryStream();
                await body.CopyToAsync(copy, cancellationToken);
                copy.Position = 0;
                var document = OutletFeeds.Load(copy);
                var kind = document.Root?.Name.LocalName is "urlset" or "sitemapindex" ? "sitemap" : "rss";
                var items = OutletFeeds.Parse(document, new NewsOutlet(page.Host, "English", "International", url, kind), DateTimeOffset.UtcNow);
                if (items.Count > 0 || OutletFeeds.SitemapChildren(document).Count > 0)
                    found.Add(new FeedCandidate(url, kind, items.Count, items.FirstOrDefault()?.Title));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException && !cancellationToken.IsCancellationRequested)
            {
            }
        }
        return (found, found.Count == 0 ? $"No working RSS feed or news sitemap found on {page.Host}." : null);
    }

    private DateTimeOffset _lastMergeCheck = DateTimeOffset.MinValue;

    // Off with the place guard (WorldNews:Grouping:PlaceGuard=false), since it applies the same rule.
    public int RecheckRecentMerges(DateTimeOffset now)
    {
        if (_options.Grouping is not { PlaceGuard: true } || now - _lastMergeCheck < RecentMergeCheck.Every) return 0;
        _lastMergeCheck = now;
        var splits = RecentMergeCheck.Run(Store, now);
        if (splits > 0) _logger.LogInformation("World News: the hourly re-check split {Count} wrongly merged stories", splits);
        return splits;
    }

    // A7/A8: once an hour, the current label of the last day's biggest stories goes into the
    // hash-chained ledger (only when it changed). From stored reports alone, so it can be re-derived.
    private DateTimeOffset _lastLabelSnapshot = DateTimeOffset.MinValue;
    public static readonly TimeSpan LabelSnapshotEvery = TimeSpan.FromHours(1);

    public int SnapshotLabels(DateTimeOffset now, bool force = false)
    {
        if (!force && now - _lastLabelSnapshot < LabelSnapshotEvery) return 0;
        _lastLabelSnapshot = now;
        var labels = Store.TopEvents(now.AddDays(-1), 150).Select(e =>
        {
            var confidence = StoryLabel.For(Store.ArticlesForEvent(e.Id), e.FirstSeen, now, out var independence);
            return (e.Id, confidence.Level.ToString(), confidence.Reason, independence.Independent, independence.Outlets);
        }).ToList();
        return Store.AppendLabelSnapshots(labels, StoryConfidenceRules.RulesVersion, now);
    }

    // A5-A10: what the methodology page shows, worked out at most once an hour.
    private (DateTimeOffset At, TrustReport Report)? _trustReport;

    public async Task<TrustReport> TrustReportAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_trustReport is { } cached && now - cached.At < TimeSpan.FromHours(1)) return cached.Report with { Challenges = Store.Challenges() };
        var week = await Task.Run(() => Store.TopEvents(now.AddDays(-7), 400), cancellationToken);
        var places = WorldNewsPlaces.Instance;
        var balanceStories = new List<(string, string)>();
        var located = new List<(string Headline, double Latitude, double Longitude, DateTimeOffset FirstSeen)>();
        foreach (var e in week)
        {
            var headline = e.Headline ?? e.Title;
            if (places.Locate(headline) is not { } place) continue;
            located.Add((headline, place.Latitude, place.Longitude, e.FirstSeen));
            var lead = await Task.Run(() => Store.ArticlesForEvent(e.Id).FirstOrDefault(), cancellationToken);
            balanceStories.Add((WorldRegions.Of(place.Country), WorldRegions.Of(lead?.Country)));
        }
        var reportsByRegion = Store.CountryHours(now.AddDays(-7)).GroupBy(row => WorldRegions.Of(row.Country))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Reports), StringComparer.OrdinalIgnoreCase);
        var recall = await EarthquakeRecallAsync(now, located, cancellationToken);
        var snapshots = Store.LabelSnapshots();
        var report = new TrustReport(now, StoryConfidenceRules.RulesVersion,
            CoverageBalance.Compute(balanceStories, reportsByRegion), recall,
            Calibration.Score(Store.CalibrationOutcomes(now.AddDays(-1))),
            snapshots.Count, LabelLedger.FirstBroken(snapshots), Store.Challenges());
        _trustReport = (now, report);
        return report;
    }

    // USGS's M4.5+ week feed (free, no key), M5.5 and up: big enough that a news service should notice.
    public const string QuakeWeekFeed = "https://earthquake.usgs.gov/earthquakes/feed/v1.0/summary/4.5_week.geojson";

    private async Task<RecallResult?> EarthquakeRecallAsync(DateTimeOffset now, IReadOnlyList<(string, double, double, DateTimeOffset)> stories, CancellationToken cancellationToken)
    {
        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            using var document = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(QuakeWeekFeed, cancellationToken));
            var records = new List<PrimarySignal>();
            foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
            {
                var properties = feature.GetProperty("properties");
                var coordinates = feature.GetProperty("geometry").GetProperty("coordinates");
                if (!properties.TryGetProperty("mag", out var mag) || mag.ValueKind != System.Text.Json.JsonValueKind.Number || mag.GetDouble() < 5.5) continue;
                var at = DateTimeOffset.FromUnixTimeMilliseconds(properties.GetProperty("time").GetInt64());
                if (at > now - TimeSpan.FromHours(6)) continue; // too recent to judge
                records.Add(new PrimarySignal("USGS", "earthquake", $"M{mag.GetDouble():0.0} {properties.GetProperty("place").GetString()}",
                    coordinates[1].GetDouble(), coordinates[0].GetDouble(), at, properties.GetProperty("url").GetString() ?? string.Empty));
            }
            return Recall.Score("USGS earthquakes M5.5+ (last 7 days)", "earthquake", records, stories);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException
            && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "World News: couldn't read USGS for the recall check");
            return null;
        }
    }

    // A3: Wikidata's current owners of the outlets World News reads, refreshed weekly. A match must
    // share its website's domain with one of the outlet's feeds ("The Guardian" is also a paper in
    // Belize); former owners (with an end date) are left out.
    public static readonly TimeSpan OwnershipRefreshEvery = TimeSpan.FromDays(7);
    public const string WikidataSparql = "https://query.wikidata.org/sparql";

    // B5: registration dates for a few domains a read (RDAP via rdap.org, key-less), then the networks.
    public const int RdapLookupsPerRead = 10;
    private IReadOnlyList<SiteSignal> _siteSignals = [];
    private IReadOnlyList<SiteNetwork> _siteNetworks = [];

    public async Task RefreshSiteNetworksAsync(CancellationToken cancellationToken = default)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        foreach (var domain in Store.SitesWithoutRegistration(RdapLookupsPerRead))
        {
            DateTimeOffset? registered = null;
            try
            {
                using var response = await http.GetAsync("https://rdap.org/domain/" + Uri.EscapeDataString(domain), cancellationToken);
                if (response.IsSuccessStatusCode) registered = SiteNetworks.Registered(await response.Content.ReadAsStringAsync(cancellationToken));
                else if ((int)response.StatusCode == 429) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                           || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogDebug(ex, "World News: RDAP lookup for {Domain} failed", domain);
                continue;
            }
            Store.SaveSiteRegistration(domain, registered, DateTimeOffset.UtcNow);
        }
        LoadSiteNetworks();
    }

    private void LoadSiteNetworks()
    {
        try
        {
            _siteSignals = Store.SiteSignals();
            _siteNetworks = SiteNetworks.Find(_siteSignals);
            SourceIndependence.NetworkOwners = SiteNetworks.Owners(_siteNetworks);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) { _logger.LogWarning(ex, "World News: couldn't load site networks"); }
    }

    // B5 for the story page: networks joining two or more of its outlets, and its new sites.
    public (IReadOnlyList<SiteNetwork> Networks, IReadOnlyList<SiteSignal> NewSites) SiteCheck(IReadOnlyCollection<string> outlets, IEnumerable<string> urls)
    {
        var domains = urls.Select(OwnershipGraph.RootDomain).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var networks = _siteNetworks.Where(n => n.Outlets.Count(o => outlets.Contains(o, StringComparer.OrdinalIgnoreCase)) >= 2).ToList();
        var newSites = _siteSignals.Where(s => domains.Contains(s.Domain) && SiteNetworks.IsNew(s, now)).ToList();
        return (networks, newSites);
    }

    private void LoadOwners()
    {
        try { SourceIndependence.ImportedOwners = OwnershipGraph.Build(Store.OutletOwners(out _)); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) { _logger.LogWarning(ex, "World News: couldn't load outlet owners"); }
    }

    public async Task<int> RefreshOwnershipAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        Store.OutletOwners(out var fetchedAt);
        if (!force && fetchedAt is { } at && DateTimeOffset.UtcNow - at < OwnershipRefreshEvery) return 0;
        var outlets = OutletFeeds.Outlets.Concat(Store.CustomFeeds()).GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(o => OwnershipGraph.RootDomain(o.Url)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var http = _httpClientFactory.CreateClient(HttpClientName);
        var rows = new List<(string Outlet, string Owner)>();
        foreach (var batch in outlets.Keys.Chunk(60))
        {
            var values = string.Join(' ', batch.Select(name => "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"@en"));
            var query = "SELECT ?name ?site ?ownerLabel WHERE { VALUES ?name { " + values + " } ?item rdfs:label ?name ; wdt:P856 ?site . "
                + "{ ?item p:P127 ?st . ?st ps:P127 ?owner } UNION { ?item p:P749 ?st . ?st ps:P749 ?owner } FILTER NOT EXISTS { ?st pq:P582 ?end } "
                + "SERVICE wikibase:label { bd:serviceParam wikibase:language \"en\". } }";
            using var request = new HttpRequestMessage(HttpMethod.Get, WikidataSparql + "?query=" + Uri.EscapeDataString(query));
            request.Headers.Accept.ParseAdd("application/sparql-results+json");
            try
            {
                using var response = await http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) { _logger.LogInformation("World News: Wikidata answered {Status}", (int)response.StatusCode); return 0; }
                using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                foreach (var binding in document.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray())
                {
                    var name = binding.GetProperty("name").GetProperty("value").GetString() ?? string.Empty;
                    var site = binding.GetProperty("site").GetProperty("value").GetString();
                    var owner = binding.GetProperty("ownerLabel").GetProperty("value").GetString() ?? string.Empty;
                    if (owner.StartsWith('Q') && owner.Skip(1).All(char.IsDigit)) continue; // no English label
                    if (outlets.TryGetValue(name, out var domains) && OwnershipGraph.RootDomain(site) is { } root && domains.Contains(root)) rows.Add((name, owner));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException
                && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(ex, "World News: couldn't read Wikidata owners");
                return 0;
            }
        }
        Store.ReplaceOutletOwners(rows, DateTimeOffset.UtcNow);
        LoadOwners();
        return rows.Count;
    }

    // B1/B2: what records and sensors saw near a story's place since two days before its first
    // report, and its physical claims checked against them. Asked after the story shows.
    // N7: a desk note, with a bell entry for each @mention of a real user. Raised through the same
    // event as every other World News alert, so the bell and Web Push pick it up.
    public DeskNote AddDeskNote(string eventId, string username, string text, Func<string, bool> knownUser)
    {
        var (note, alerts) = Store.AddDeskNote(eventId, username, text, DateTimeOffset.UtcNow, knownUser);
        foreach (var alert in alerts) AlertCreated?.Invoke(alert.Username, alert);
        return note;
    }

    public IReadOnlyList<PublicLiveStory> PublicLiveStories(int page = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        return Store.PublicLiveStories(DateTimeOffset.UtcNow, checked(page * LiveUpdates.PageSize), LiveUpdates.PageSize + 1);
    }

    public IReadOnlyList<PublicCorrection> PublicCorrections(int page = 0, string? eventId = null, string? id = null, Guid? articleId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        return Store.PublicCorrections(checked(page * Corrections.PageSize), Corrections.PageSize + 1, eventId, id, articleId);
    }

    public (PublicLiveStory Story, IReadOnlyList<PublicLiveUpdate> Updates)? PublicLiveBlog(string eventId, int page = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        var now = DateTimeOffset.UtcNow;
        var story = Store.PublicLiveStories(now, limit: 1, eventId: eventId).SingleOrDefault();
        if (story is null) return null;
        var updates = Store.PublicLiveUpdates(eventId, now, checked(page * LiveUpdates.PageSize), LiveUpdates.PageSize + 1);
        return updates.Count == 0 ? null : (story, updates);
    }

    // N6: the handover note for the last `hours`.
    public string HandoverMarkdown(int hours)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddHours(-Math.Clamp(hours, 1, 72));
        var titles = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? TitleOf(string eventId)
        {
            if (!titles.TryGetValue(eventId, out var title)) titles[eventId] = title = Store.Event(eventId) is { } e ? e.Headline ?? e.Title : null;
            return title;
        }
        return ShiftHandover.Markdown(since, now, Store.Budget(), Store.Holds(),
            Store.LabelSnapshots(since: since, limit: 1000),
            Store.Challenges().Where(c => c.Status == "open").ToList(),
            Store.Tips().Where(t => t.CreatedAt >= since).ToList(),
            Store.DeskNotes(limit: 200), Store.LiveUpdates(limit: 200, orderByActivity: true), TitleOf, at => at.ToLocalGwsDateTime(),
            Store.BudgetLabelLevels());
    }

    // B3: a story's pictures that were used earlier in other stories, and how many carry Content
    // Credentials. The archive of prints is read at most every ten minutes.
    private (DateTimeOffset At, IReadOnlyList<PhotoPrint> Prints)? _photoArchive;

    public (IReadOnlyList<RecycledPhoto> Recycled, int Pictures, int WithCredentials) PhotoCheck(string eventId, DateTimeOffset firstSeen)
    {
        var story = Store.Photos(eventId);
        if (story.Count == 0) return ([], 0, 0);
        var now = DateTimeOffset.UtcNow;
        if (_photoArchive is not { } archive || now - archive.At > TimeSpan.FromMinutes(10))
            _photoArchive = archive = (now, Store.Photos());
        return (RecycledPhotos.Find(story, archive.Prints, firstSeen), story.DistinctBy(p => p.Hash).Count(), story.Count(p => p.Credentials));
    }

    // B8: the country a story happened in (from its headline), for local-first ordering; null when unknown.
    public static string? StoryCountry(StoryDocument story) =>
        WorldNewsPlaces.Instance.Locate(story.Event.Headline ?? story.Event.Title)?.Country;

    public async Task<EvidenceBoard?> EvidenceBoardAsync(StoryDocument story, CancellationToken cancellationToken = default)
    {
        var headline = story.Event.Headline ?? story.Event.Title;
        if (WorldNewsPlaces.Instance.Locate(headline) is not { } place) return null;
        var radius = place.Precision == WorldNewsPrecision.City ? EvidenceBoards.CityKilometres : EvidenceBoards.CountryKilometres;
        var signals = new List<PrimarySignal>();
        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        async Task Gather(Func<Task<IEnumerable<GwsBusinessSuite.Application.Hazards.HazardFeature>>> read)
        {
            try { signals.AddRange((await read()).Select(f => new PrimarySignal(SourceName(f.Url), f.Kind, f.Title, f.Latitude, f.Longitude, f.ObservedAt, f.Url))); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(ex, "World News: an evidence source couldn't be read");
            }
        }
        var span = radius / 111.0 + 1;
        var box = new GwsBusinessSuite.Application.CameraIntel.BoundingBox(Math.Min(85, place.Latitude + span), Math.Max(-85, place.Latitude - span),
            Math.Min(180, place.Longitude + span), Math.Max(-180, place.Longitude - span));
        await Task.WhenAll(
            Gather(async () => (await services.GetRequiredService<GwsBusinessSuite.Application.Hazards.HazardLayerService>().GetHazardsAsync(box, cancellationToken)).Features),
            Gather(async () => await services.GetRequiredService<GwsBusinessSuite.Application.Hazards.FireHotspotService>().GetAsync(cancellationToken)),
            Gather(async () => await services.GetRequiredService<InternetOutageService>().GetAsync(cancellationToken)),
            Gather(async () => await services.GetRequiredService<GwsBusinessSuite.Application.LiveLayers.AirportStatusService>().GetAsync(cancellationToken)));
        var nearby = EvidenceBoards.Near(signals, place.Latitude, place.Longitude, story.Event.FirstSeen, DateTimeOffset.UtcNow, radius);
        return new EvidenceBoard(SensorChecks.Relevant(headline, nearby), SensorChecks.Check(headline, nearby), place.City is { } city ? $"{city}, {place.Country}" : place.Country);
    }

    // The reader has seen this analysis: the next "what changed" starts from it.
    public void MarkStoryRead(string username, string eventId, DateTimeOffset analysisAt) =>
        Store.MarkStoryRead(username, eventId, analysisAt, DateTimeOffset.UtcNow);

    // ---- SentinelGPT's analysis of an event (06-11-26), written when its document is read.

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<EventAnalysis>>> _analysing = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _analysisStatus = new(StringComparer.Ordinal);

    // True when there's none yet, or coverage has grown by a quarter since.
    public bool AnalysisDue(string eventId) =>
        Store.Event(eventId) is { } e && EventAnalyzer.IsDue(Store.LatestAnalysis(eventId), e);

    // What a running analysis is doing, for the page; null when none is running.
    public string? AnalysisStatus(string eventId) =>
        _analysing.ContainsKey(eventId) ? _analysisStatus.GetValueOrDefault(eventId) ?? "Starting…" : null;

    // Reads the outlets' own articles, then asks SentinelGPT to compare the coverage, and keeps the
    // answer. The model runs where the app's shared work on public data runs: with server AI off
    // (production), on the Admin Mac whose app is open (MacRelayScope), else the server's own. One
    // analysis per event at a time; anyone else asking waits for the same one. It carries on if the
    // person who asked closes the page. Throws InvalidOperationException (ServerAiUnavailableException
    // when no Mac is open) with a message to show.
    public Task<EventAnalysis> AnalyzeAsync(string eventId) =>
        _analysing.GetOrAdd(eventId, id => new Lazy<Task<EventAnalysis>>(() => RunAnalysisAsync(id))).Value;

    private async Task<EventAnalysis> RunAnalysisAsync(string eventId)
    {
        try
        {
            var e = Store.Event(eventId) ?? throw new InvalidOperationException("This event isn't there any more (events are merged and pruned as coverage changes).");
            var articles = Store.ArticlesForEvent(eventId);
            IReadOnlyDictionary<string, string> excerpts = new Dictionary<string, string>();
            if (_articleReader is not null)
            {
                _analysisStatus[eventId] = $"Reading the outlets' own articles (up to {WorldNewsArticleReader.MaxArticles})…";
                excerpts = await Task.Run(() => _articleReader.ReadAsync(Store, articles, CancellationToken.None));
            }
            var prompt = EventAnalyzer.BuildPrompt(e, articles, excerpts, at => at.ToLocalGwsDateTime());
            _analysisStatus[eventId] = $"SentinelGPT is comparing what {e.OutletCount} outlets report ({excerpts.Count} articles read in full)…";
            string raw;
            using (MacRelayScope.ForSharedWork())
            {
                await using var scope = _scopes.CreateAsyncScope();
                // The Mac-first failover gives a stalled Mac attempt two minutes, then hands the
                // same request to the droplet. Keep a separate overall budget long enough for that
                // backup to finish instead of cancelling exactly when failover is needed.
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                raw = await scope.ServiceProvider.GetRequiredService<IOllamaService>()
                    .GenerateAsync(_model, EventAnalyzer.SystemPrompt, prompt, 16_384, limit.Token);
            }
            var outlets = articles.Select(a => a.Outlet).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (EventAnalyzer.Parse(raw, outlets) is not { } parsed)
            {
                var preview = string.IsNullOrWhiteSpace(raw) ? "(empty)" : raw.Length <= 300 ? raw : raw[..300];
                _logger.LogWarning("World News analysis of {Event} returned unparseable output ({Length} chars): {Preview}", eventId, raw?.Length ?? 0, preview);
                throw new InvalidOperationException("SentinelGPT's answer didn't have the analysis in it. Try again.");
            }
            var writtenBy = _serverHasNoModel ? "SentinelGPT on the Mac" : _model;
            var saved = Store.SaveAnalysis(new EventAnalysis(eventId, parsed.Claim, parsed.Summary, parsed.Agreed, parsed.Differences, writtenBy,
                e.ArticleCount, excerpts.Count, DateTimeOffset.UtcNow, EventAnalyzer.ParseDisputes(raw, outlets)));
            return saved;
        }
        catch (ServerAiUnavailableException ex)
        {
            // Production has no model on the server: the analysis waits for the Mac.
            throw new InvalidOperationException("SentinelGPT writes this on your Mac, and no Mac is connected right now. Open the GWS Suite Mac app " +
                "(signed in as an admin), then choose Try again. The outlets, the figures and the sources below don't need it.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException("SentinelGPT took too long on both the Mac and its AI-droplet backup (over 6 minutes). Try again in a moment.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "World News analysis of {Event} failed", eventId);
            throw new InvalidOperationException($"SentinelGPT couldn't be reached: {ex.Message}", ex);
        }
        finally
        {
            _analysing.TryRemove(eventId, out _);
            _analysisStatus.TryRemove(eventId, out _);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("[^a-z0-9]+")]
    private static partial System.Text.RegularExpressions.Regex NonSlug();

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _pulling.Dispose();
        _regrouping.Dispose();
        _ollama.Dispose();
        _running.Dispose();
        _englishRunning.Dispose();
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

// Reads the news every few minutes (WorldNewsService.ReadInterval), starting a minute after the app
// starts (WorldNews:Enabled turns it off; WorldNews:StartupDelaySeconds changes the wait).
public sealed class WorldNewsBackgroundService(
    WorldNewsService news,
    IConfiguration configuration,
    ILogger<WorldNewsBackgroundService> logger) : BackgroundService
{
    internal static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("WorldNews:Enabled", true)) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(configuration.GetValue("WorldNews:StartupDelaySeconds", 60)), stoppingToken);
            await Task.WhenAll(
                RunResilientLoopAsync(ReadOnceAsync, "news refresh", FailureRetryDelay, logger, stoppingToken),
                RunResilientLoopAsync(EnglishOnceAsync, "English processing", FailureRetryDelay, logger, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<TimeSpan> ReadOnceAsync(CancellationToken cancellationToken)
    {
        await news.RefreshAsync(cancellationToken);
        return news.ReadInterval;
    }

    private async Task<TimeSpan> EnglishOnceAsync(CancellationToken cancellationToken)
    {
        var madeProgress = await news.ProcessEnglishAsync(cancellationToken);
        return TimeSpan.FromSeconds(madeProgress ? 5 : 60);
    }

    internal static async Task RunResilientLoopAsync(
        Func<CancellationToken, Task<TimeSpan>> iteration,
        string workerName,
        TimeSpan failureRetryDelay,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delay = await iteration(cancellationToken);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "World News {WorkerName} worker failed; retrying in {Delay}", workerName, failureRetryDelay);
                try
                {
                    await Task.Delay(failureRetryDelay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
