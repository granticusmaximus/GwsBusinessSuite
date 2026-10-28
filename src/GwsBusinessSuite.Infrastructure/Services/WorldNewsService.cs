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
    private readonly ILogger<WorldNewsService> _logger;

    public WorldNewsService(IConfiguration configuration, IOptions<ContentStudioOptions> ollamaOptions,
        IHttpClientFactory httpClientFactory, IServiceScopeFactory scopes, ILogger<WorldNewsService> logger)
    {
        _logger = logger;
        var path = Path.GetFullPath(configuration["WorldNews:DatabasePath"] ?? "/app/data/worldnews.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Store = new WorldNewsStore(path);
        var baseUrl = string.IsNullOrWhiteSpace(ollamaOptions.Value.BaseUrl) ? ContentStudioOptions.DefaultBaseUrl : ollamaOptions.Value.BaseUrl;
        _ollama = new OllamaClient(new Uri(baseUrl.TrimEnd('/') + "/"), new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        var http = httpClientFactory.CreateClient(HttpClientName);
        _pipeline = new WorldNewsPipeline(Store, http, new GdeltDocClient(http, TimeProvider.System), _ollama, TimeProvider.System,
            new WorldNewsOptions { MaxEmbedPerCycle = configuration.GetValue("WorldNews:MaxEmbedPerCycle", 1500) });
        // IOllamaService is a typed HttpClient, so each summary takes a fresh one rather than this
        // singleton holding one for the app's lifetime. 8k context fits SummaryOptions.MaxPromptChars.
        var model = ollamaOptions.Value.Model;
        _summarizer = new EventSummarizer(Store, async (system, prompt, ct) =>
            {
                await using var scope = scopes.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IOllamaService>().GenerateAsync(model, system, prompt, 8192, ct);
            }, model, TimeProvider.System, at => at.ToLocalGwsDateTime(),
            new SummaryOptions { PerRun = configuration.GetValue("WorldNews:SummariesPerRead", 3) });
        LastRefreshed = Store.LastFetched(LastReadKey);
        if (LastRefreshed is not null) Status = string.Empty;
        _eventLists = new EventListCache(since => Store.TopEvents(since, MaxEvents), TimeProvider.System, Interval);
        _situationLists = new RangeListCache<SituationOverview>(since => Store.TopSituations(since, MaxEvents), TimeProvider.System, Interval);
        // Built at startup, so the first visit after a deploy doesn't wait for it.
        WarmEventLists();
    }

    // The busiest events in the last rangeHours, from memory unless a read has changed them.
    public IReadOnlyList<EventOverview> TopEvents(int rangeHours) => _eventLists.Get(rangeHours);

    // Developing stories with news in the last rangeHours, likewise kept in memory between reads.
    public IReadOnlyList<SituationOverview> TopSituations(int rangeHours) => _situationLists.Get(rangeHours);

    public WorldSnapshot World(int rangeHours) => Store.World(DateTimeOffset.UtcNow.AddHours(-rangeHours));

    private void WarmEventLists() => _ = Task.Run(() =>
    {
        try { _eventLists.Get(24); }
        catch (Exception ex) { _logger.LogWarning(ex, "Building the world news event list failed"); }
    });

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
            var alerts = Store.CreateWatchAlerts(DateTimeOffset.UtcNow);
            foreach (var alert in alerts) AlertCreated?.Invoke(alert.Username, alert);
            LastRefreshed = DateTimeOffset.UtcNow;
            Store.SetLastFetched(LastReadKey, LastRefreshed.Value);
            _eventLists.Clear();
            _situationLists.Clear();
            Status = "Summarizing the biggest events...";
            Changed?.Invoke();
            var summaries = await Task.Run(() => _summarizer.RunAsync(ct), ct);
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
            _eventLists.Clear();
            _situationLists.Clear();
            Changed?.Invoke();
        }
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
