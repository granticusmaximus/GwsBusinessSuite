using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.App;

// World news (.claude/plans/world-news-situations-macos-2026-10.md): WorldNewsKit's pipeline,
// run on this Mac with the local Ollama doing the embeddings. It refreshes every 15 minutes once
// the World tab has been opened, so the app doesn't start an 80-second first read at launch.
public sealed class WorldNewsService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly WorldNewsPipeline _pipeline;
    private CancellationTokenSource? _loop;

    public WorldNewsService(OllamaClient ollama)
    {
        var folder = Path.Combine(FileSystem.Current.AppDataDirectory, "worldnews");
        Directory.CreateDirectory(folder);
        Store = new WorldNewsStore(Path.Combine(folder, "world.db"));
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh) GwsBusinessSuite-WorldNews/1.0");
        _pipeline = new WorldNewsPipeline(Store, http, new GdeltDocClient(http, TimeProvider.System), ollama,
            TimeProvider.System, new WorldNewsOptions());
    }

    public WorldNewsStore Store { get; }
    public string Status { get; private set; } = "Not read yet.";
    public DateTimeOffset? LastRefreshed { get; private set; }
    public bool IsRefreshing => _running.CurrentCount == 0;
    public event Action? Changed;

    public void StartAutoRefresh()
    {
        if (_loop is not null) return;
        _loop = new CancellationTokenSource();
        _ = LoopAsync(_loop.Token);
    }

    public async Task RefreshAsync()
    {
        if (!await _running.WaitAsync(0)) return;
        try
        {
            Status = "Reading the world's news...";
            Changed?.Invoke();
            // The pipeline does synchronous SQLite work and CPU-bound grouping, so keep it off the UI thread.
            var report = await Task.Run(() => _pipeline.RunCycleAsync(CancellationToken.None));
            LastRefreshed = DateTimeOffset.Now;
            var problems = report.Notes.Count == 0 ? string.Empty
                : $" {report.Notes.Count} {(report.Notes.Count == 1 ? "problem" : "problems")}: {report.Notes[0]}";
            Status = $"{report.Added:N0} new reports, {report.NewEvents:N0} new events.{problems}";
        }
        catch (Exception ex)
        {
            Status = "Couldn't read the news: " + ex.Message;
        }
        finally
        {
            _running.Release();
            Changed?.Invoke();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync();
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
