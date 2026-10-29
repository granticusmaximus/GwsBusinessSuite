using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.App;

// World News summaries written on this Mac (29-10-26). The droplet's small model mixes up who did
// what and gemma4 doesn't fit there, so while this app runs it asks the server (with the
// Workspace tab's session) every 15 minutes for events due a summary, answers each with the
// local model and sends the answer back. While it keeps asking, the server's own model stands
// aside; if the app is closed for two hours, the server takes over again.
public sealed class WorldNewsSummaryRelay(NativeSessionClient session, OllamaClient ollama)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    // The most accurate local models first. A small model is no better than the server's, so
    // without one of these the relay stays idle and the server keeps writing.
    private static readonly string[] PreferredModels = ["gemma4", "gemma3:27b", "qwen3:32b", "gemma3:12b", "qwen3:14b"];
    private const int JobsPerRound = 5;
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
        await Task.Delay(TimeSpan.FromSeconds(60));
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
        try
        {
            if (await PickModelAsync(cancellationToken) is not { } model)
            {
                Status = "No accurate local model installed (gemma4 or similar), so the server writes World News summaries.";
                return 0;
            }
            var (jobs, error) = await session.GetWorldNewsSummaryJobsAsync(JobsPerRound, cancellationToken);
            if (jobs is null)
            {
                Status = $"World News summaries paused: {error}";
                return 0;
            }
            var written = 0;
            foreach (var job in jobs)
            {
                var answer = await ollama.ChatAsync(model,
                    [new OllamaChatMessage("system", job.SystemPrompt), new OllamaChatMessage("user", job.Prompt)], [], cancellationToken, think: false);
                if (await session.SaveWorldNewsSummaryAsync(job, model, answer.Content, cancellationToken) is null) written++;
            }
            Status = $"World News: {written} summar{(written == 1 ? "y" : "ies")} written with {model} at {DateTime.Now:HH:mm}.";
            return written;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Status = $"World News summaries paused: {ex.Message}";
            return 0;
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<string?> PickModelAsync(CancellationToken cancellationToken)
    {
        var installed = await ollama.ListModelsAsync(cancellationToken);
        return PreferredModels
            .Select(wanted => installed.FirstOrDefault(name => name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(wanted + ":", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(name => name is not null);
    }
}
