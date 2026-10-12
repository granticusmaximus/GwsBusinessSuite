using System.Net.Http.Json;
using System.Text.Json;
using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.App;

// Overwatch camera watch, run on this Mac: every few minutes, read each watched camera (the
// server's list: favorites, then cameras in watched areas) with a local Ollama vision model and
// post what it saw. The server decides whether anything changed enough to alert (see
// CameraWatchService). Needs the Workspace tab signed in and a vision model in Ollama (e.g.
// gemma4); while either is missing it simply waits and tries again later.
public sealed class CameraWatcher(NativeSessionClient session, OllamaClient ollama)
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BetweenCameras = TimeSpan.FromSeconds(3);
    private readonly HttpClient _ollamaHttp = new() { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromMinutes(2) };
    private CancellationTokenSource? _stop;

    public string Status { get; private set; } = "Not started";

    public void Start()
    {
        if (_stop is not null) return;
        _stop = new CancellationTokenSource();
        _ = RunAsync(_stop.Token);
    }

    public void Stop()
    {
        _stop?.Cancel();
        _stop = null;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        // Let the Workspace tab finish signing in before the first round.
        await Delay(TimeSpan.FromMinutes(1), stop);
        while (!stop.IsCancellationRequested)
        {
            var interval = DefaultInterval;
            try
            {
                interval = await RoundAsync(stop) ?? DefaultInterval;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Status = $"Camera watch error: {ex.Message}";
            }
            await Delay(interval, stop);
        }
    }

    // One pass over the watch list. Returns the server's interval, or null to use the default.
    private async Task<TimeSpan?> RoundAsync(CancellationToken stop)
    {
        var model = await VisionModelAsync(stop);
        if (model is null)
        {
            Status = "Camera watch is waiting for a vision model in Ollama (e.g. gemma4).";
            return null;
        }
        var (json, error) = await session.GetCameraWatchListAsync(stop);
        if (json is not { } list)
        {
            Status = $"Camera watch is waiting: {error}";
            return null;
        }
        var system = list.GetProperty("systemPrompt").GetString() ?? string.Empty;
        var prompt = list.GetProperty("userPrompt").GetString() ?? string.Empty;
        var interval = TimeSpan.FromMinutes(Math.Clamp(list.GetProperty("intervalMinutes").GetInt32(), 5, 60));
        var cameras = list.GetProperty("cameras").EnumerateArray()
            .Select(camera => camera.GetProperty("id").GetString())
            .OfType<string>().ToList();
        var read = 0;
        foreach (var cameraId in cameras)
        {
            stop.ThrowIfCancellationRequested();
            var frame = await session.GetCameraWatchSnapshotAsync(cameraId, stop);
            if (frame is not { Length: > 0 }) continue;
            var answer = await AskAsync(model, system, prompt, frame, stop);
            if (answer is null) continue;
            if (await session.PostCameraWatchReadingAsync(cameraId, answer, frame, stop) is null) read++;
            await Delay(BetweenCameras, stop);
        }
        Status = $"Camera watch read {read} of {cameras.Count} cameras at {DateTime.Now:HH:mm} with {model}.";
        return interval;
    }

    private async Task<string?> VisionModelAsync(CancellationToken stop)
    {
        try
        {
            var models = await ollama.ListModelDetailsAsync(stop);
            return models.FirstOrDefault(m => m.Capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase))?.Name;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !stop.IsCancellationRequested)
        {
            return null;
        }
    }

    // Ollama's /api/generate with the image attached; JSON mode keeps small models on the schema.
    private async Task<string?> AskAsync(string model, string system, string prompt, byte[] frame, CancellationToken stop)
    {
        try
        {
            using var response = await _ollamaHttp.PostAsJsonAsync("api/generate", new
            {
                model, system, prompt, images = new[] { Convert.ToBase64String(frame) },
                stream = false, format = "json", think = false, options = new { temperature = 0 }
            }, stop);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop));
            return document.RootElement.TryGetProperty("response", out var text) ? text.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !stop.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken stop)
    {
        try { await Task.Delay(delay, stop); }
        catch (TaskCanceledException) { }
    }
}
