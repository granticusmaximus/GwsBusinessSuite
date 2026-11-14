using System.Net.Http.Json;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// Lazy model loading (13-11-26, Grant: faster SentinelGPT answers). A model is loaded on the machine
// that will run it only when someone is about to need it, not all the time:
//   - opening SentinelGPT loads its model on the connection that answers (the Mac when it's up, else
//     the AI droplet), so the first question doesn't wait 10-30 s for a cold load;
//   - turning on Deep Analysis loads its two advisers on the Mac, where they run;
//   - when the Mac comes back and SentinelGPT was used in the last half hour, the model is loaded
//     there before the next question arrives.
// Each check asks Ollama what it already holds (/api/ps), so a loaded model costs one small request,
// and a connection+model is checked at most every two minutes. Loading goes straight to Ollama (not
// through the work queue), since waiting behind a long background job would defeat it.
public sealed class SentinelModelLoader
{
    public const string SingleClient = "ollama-loader";
    public const string KeepAlive = "30m";
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan UsedRecently = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(3);

    private readonly IHttpClientFactory _clients;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SentinelModelLoader> _logger;
    private readonly TimeProvider _time;
    private readonly OllamaFailoverMonitor? _monitor;
    private readonly ServerAiOptions _serverAi;
    private readonly string? _serverDefault;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastUsed = DateTimeOffset.MinValue;

    public SentinelModelLoader(IHttpClientFactory clients, IServiceScopeFactory scopes, ILogger<SentinelModelLoader> logger,
        IOptions<ServerAiOptions> serverAi, IOptions<ContentStudioOptions> contentStudio, TimeProvider? time = null, OllamaFailoverMonitor? monitor = null)
    {
        _clients = clients;
        _scopes = scopes;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _monitor = monitor;
        _serverAi = serverAi.Value;
        _serverDefault = contentStudio.Value.Model;
        if (_monitor is not null) _monitor.PrimaryChanged += OnPrimaryChanged;
    }

    // With no server model (Mac-relay mode) there's nothing here to load.
    public bool Enabled => _serverAi.Mode != ServerAiMode.None;

    // The connection SentinelGPT's next answer comes from.
    public string Connection => _monitor is null ? SingleClient : _monitor.PrimaryUp ? OllamaFailoverMonitor.PrimaryClient : OllamaFailoverMonitor.SecondaryClient;

    // Someone opened SentinelGPT: load its model where it will run. Never waits, never throws.
    public void SentinelOpened()
    {
        if (!Enabled) return;
        lock (_gate) _lastUsed = _time.GetUtcNow();
        _ = Task.Run(async () => await LoadChosenModelAsync(Connection, CancellationToken.None));
    }

    // Deep Analysis was switched on: its advisers run on the Mac, so load them there.
    public void DeepAnalysisOn()
    {
        if (!Enabled) return;
        var connection = _monitor is null ? SingleClient : _monitor.PrimaryUp ? OllamaFailoverMonitor.PrimaryClient : null;
        if (connection is null) return;
        foreach (var model in new[] { SentinelGptDefaults.CodeReviewAdviserModel, SentinelGptDefaults.ReasoningAdviserModel })
            _ = Task.Run(async () => await LoadAsync(connection, model, fallback: null, CancellationToken.None));
    }

    private void OnPrimaryChanged(bool up)
    {
        DateTimeOffset lastUsed;
        lock (_gate) lastUsed = _lastUsed;
        if (!up || !Enabled || _time.GetUtcNow() - lastUsed > UsedRecently) return;
        _ = Task.Run(async () => await LoadChosenModelAsync(OllamaFailoverMonitor.PrimaryClient, CancellationToken.None));
    }

    public async Task<string> LoadChosenModelAsync(string connection, CancellationToken ct)
    {
        string model;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var settings = await scope.ServiceProvider.GetRequiredService<ISiteSettingsService>().GetSettingsAsync(ct);
            model = string.IsNullOrWhiteSpace(settings.OllamaModelOverride) ? SentinelGptDefaults.Model : settings.OllamaModelOverride;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Model loader: couldn't read the chosen model");
            model = SentinelGptDefaults.Model;
        }
        return await LoadAsync(connection, model, _serverDefault, ct);
    }

    // What happened, for logs and tests: "loaded", "already loaded", "recently checked", "busy",
    // "not installed" or "unreachable".
    public async Task<string> LoadAsync(string connection, string model, string? fallback, CancellationToken ct)
    {
        var key = connection + "|" + model;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_checked.TryGetValue(key, out var at) && now - at < RecheckAfter) return "recently checked";
            if (!_loading.Add(key)) return "busy";
            _checked[key] = now;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LoadTimeout);
            var http = _clients.CreateClient(connection);
            var installed = Names(await http.GetStringAsync("api/tags", timeout.Token));
            // As OllamaService does: a model this machine hasn't got is answered by the server default.
            var name = installed.FirstOrDefault(n => SameModel(n, model))
                ?? (fallback is { Length: > 0 } ? installed.FirstOrDefault(n => SameModel(n, fallback)) : null);
            if (name is null) return "not installed";
            if (Names(await http.GetStringAsync("api/ps", timeout.Token)).Any(n => SameModel(n, name))) return "already loaded";
            var started = _time.GetTimestamp();
            using var response = await http.PostAsJsonAsync("api/generate", new { model = name, keep_alive = KeepAlive, stream = false }, timeout.Token);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("SentinelGPT: loaded {Model} on {Connection} in {Seconds:0.0} s, ahead of the first question",
                name, connection, _time.GetElapsedTime(started).TotalSeconds);
            return "loaded";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
                                       && !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Model loader: {Connection} didn't load {Model}", connection, model);
            return "unreachable";
        }
        finally
        {
            lock (_gate) _loading.Remove(key);
        }
    }

    // Model names from /api/tags or /api/ps: {"models":[{"name":"sentinelgpt:latest",...}]}.
    public static IReadOnlyList<string> Names(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
        return models.EnumerateArray()
            .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : m.TryGetProperty("model", out var id) ? id.GetString() : null)
            .OfType<string>().ToList();
    }

    private static bool SameModel(string a, string b) => OllamaModelWarmupBackgroundService.ModelNamesMatch(a, b);
}
