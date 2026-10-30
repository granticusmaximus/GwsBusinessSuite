using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.App;

// World News headlines embedded on this Mac (30-10-26). Embedding about 15,000 headlines a day was
// the droplet's steadiest Ollama load, and it slowed the whole app. While this app runs and has the
// server's embedding model installed, it asks every few minutes for headlines waiting, embeds them
// locally and sends the vectors back. The server leaves embedding to it while it keeps asking, and
// takes over again ten minutes after it stops.
public sealed class WorldNewsEmbedRelay(NativeSessionClient session, OllamaClient ollama)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);
    private const int BatchSize = 64;
    // Each batch is one request against the admin's per-minute limit, which the browser shares.
    private const int BatchesPerRound = 8;
    private readonly SemaphoreSlim _running = new(1, 1);
    private bool _started;

    public string Status { get; private set; } = "Not started.";

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        // After the offline sync's first round, once the Workspace tab has signed in.
        await Task.Delay(TimeSpan.FromSeconds(90));
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await RunOnceAsync();
        }
        while (await timer.WaitForNextTickAsync());
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await _running.WaitAsync(0, cancellationToken)) return 0;
        var embedded = 0;
        try
        {
            var installed = await ollama.ListModelsAsync(cancellationToken);
            for (var batch = 0; batch < BatchesPerRound; batch++)
            {
                var (model, jobs, error) = await session.GetWorldNewsEmbedJobsAsync(installed, BatchSize, cancellationToken);
                if (jobs is null || model is null)
                {
                    Status = $"World News embedding paused: {error}";
                    return embedded;
                }
                if (InstalledName(installed, model) is not { } local)
                {
                    Status = $"The server groups World News with {model}, which isn't installed here, so it embeds the headlines itself.";
                    return embedded;
                }
                if (jobs.Count == 0) break;

                var vectors = await ollama.EmbedAsync(local, jobs.Select(job => job.Title).ToList(), cancellationToken);
                if (await session.SaveWorldNewsEmbeddingsAsync(model, jobs.Select((job, i) => (job.Id, vectors[i])).ToList(), cancellationToken) is { } saveError)
                {
                    Status = $"World News embedding paused: {saveError}";
                    return embedded;
                }
                embedded += jobs.Count;
                if (jobs.Count < BatchSize) break;
            }
            Status = $"World News: {embedded} headline{(embedded == 1 ? "" : "s")} embedded here at {DateTime.Now:HH:mm}.";
            return embedded;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Status = $"World News embedding paused: {ex.Message}";
            return embedded;
        }
        finally
        {
            _running.Release();
        }
    }

    // "embeddinggemma" is installed as "embeddinggemma" or "embeddinggemma:latest" (the server
    // matches the same way).
    private static string? InstalledName(IEnumerable<string> installed, string model) =>
        installed.FirstOrDefault(name => name.Equals(model, StringComparison.OrdinalIgnoreCase)
            || (!model.Contains(':') && name.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase)));
}
