using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// World News (Intelligence workspace, .claude/plans/world-news-situations-macos-2026-10.md):
// WorldNewsKit's pipeline on the server. 41 Google News country editions are read every 15
// minutes and grouped into events across languages with embeddinggemma on the server's Ollama.
// embeddinggemma is in ollama/server-models.txt, and Light mode allows it, since it's embeddings,
// not generation. The data lives in its own SQLite file beside the main database (no
// migrations), keeping 30 days.
public sealed class WorldNewsService : IDisposable
{
    public const string HttpClientName = "world-news";
    // The last finished read is kept in the news database, so a restart doesn't show "Not read yet".
    private const string LastReadKey = "world-news:last-read";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly OllamaClient _ollama;
    private readonly WorldNewsPipeline _pipeline;
    private readonly ILogger<WorldNewsService> _logger;

    public WorldNewsService(IConfiguration configuration, IOptions<ContentStudioOptions> ollamaOptions,
        IHttpClientFactory httpClientFactory, ILogger<WorldNewsService> logger)
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
        LastRefreshed = Store.LastFetched(LastReadKey);
        if (LastRefreshed is not null) Status = string.Empty;
    }

    public WorldNewsStore Store { get; }
    public string Status { get; private set; } = "Not read yet.";
    public DateTimeOffset? LastRefreshed { get; private set; }
    public bool IsRefreshing => _running.CurrentCount == 0;
    public event Action? Changed;

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
            LastRefreshed = DateTimeOffset.UtcNow;
            Store.SetLastFetched(LastReadKey, LastRefreshed.Value);
            var problems = report.Notes.Count == 0 ? string.Empty
                : $" {report.Notes.Count} {(report.Notes.Count == 1 ? "problem" : "problems")}: {report.Notes[0]}";
            Status = $"{report.Added:N0} new reports, {report.NewEvents:N0} new events.{problems}";
            if (report.Notes.Count > 0)
                _logger.LogWarning("World news read finished with {Count} problem(s): {Notes}", report.Notes.Count, string.Join(" | ", report.Notes));
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
            Changed?.Invoke();
        }
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
