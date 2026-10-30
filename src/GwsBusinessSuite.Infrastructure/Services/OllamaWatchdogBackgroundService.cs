using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.DockerHealth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// Restarts the Ollama container when it has stopped answering, so nobody has to do it by hand
// (Grant was restarting it every time he opened the app, 30-10-26). Every 30 seconds it asks
// Ollama for its version with a short timeout; four failures in a row, or one job holding the
// app's single model slot for longer than anything legitimate takes, restarts the container through
// the Docker socket the health monitor already uses. Restarts are at least 15 minutes apart, and each
// is logged and recorded in Docker Health's action log. Off where no Ollama container is visible
// (local development), or with Ollama:Watchdog:Enabled=false.
public sealed class OllamaWatchdogBackgroundService(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClientFactory,
    OllamaWorkloadScheduler workloads,
    IOptions<ContentStudioOptions> ollamaOptions,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<OllamaWatchdogBackgroundService> logger) : BackgroundService
{
    public const string HttpClientName = "ollama-watchdog";
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public const int FailuresBeforeRestart = 4;
    // Every generation has its own limit (Settings > AI's timeout, 20 minutes unless changed); ten
    // minutes past it, one job still holding the model is a wedged model, not a slow one.
    public static readonly TimeSpan StuckGrace = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    private int _failures;
    private DateTimeOffset? _lastRestart;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Ollama:Watchdog:Enabled", true)) return;
        try
        {
            await Task.Delay(InitialDelay, time, stoppingToken);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Ollama watchdog check failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Public for tests: one check, and whether it restarted the container.
    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        var answered = await AnswersAsync(cancellationToken);
        _failures = answered ? 0 : _failures + 1;
        var heldFor = workloads.HeldSince is { } since ? time.GetUtcNow() - since : TimeSpan.Zero;
        var stuckAfter = heldFor > StuckGrace ? await StuckAfterAsync(cancellationToken) : TimeSpan.MaxValue;
        var reason = _failures >= FailuresBeforeRestart ? $"it hasn't answered for {_failures} checks in a row"
            : heldFor > stuckAfter ? $"one job has held the model for {heldFor.TotalMinutes:0} minutes, past its {stuckAfter.TotalMinutes - StuckGrace.TotalMinutes:0}-minute limit"
            : null;
        if (reason is null) return false;
        if (_lastRestart is { } last && time.GetUtcNow() - last < Cooldown) return false;

        await using var scope = scopes.CreateAsyncScope();
        var docker = scope.ServiceProvider.GetRequiredService<IDockerHealthService>();
        var containers = await docker.ListContainersAsync(cancellationToken);
        var ollama = containers.Containers.FirstOrDefault(c => c.Image.StartsWith("ollama/ollama", StringComparison.OrdinalIgnoreCase));
        if (!containers.Available || ollama is null) return false;

        logger.LogWarning("Restarting the Ollama container {Container}: {Reason}", ollama.Name, reason);
        _lastRestart = time.GetUtcNow();
        _failures = 0;
        var result = await docker.RestartContainerAsync(ollama.Name.TrimStart('/'), "ollama-watchdog", cancellationToken);
        if (!result.Succeeded) logger.LogWarning("The Ollama restart didn't go through: {Message}", result.Message);
        return result.Succeeded;
    }

    private async Task<TimeSpan> StuckAfterAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var settings = await scope.ServiceProvider.GetRequiredService<GwsBusinessSuite.Application.Settings.ISiteSettingsService>()
            .GetSettingsAsync(cancellationToken);
        var limit = Math.Max(settings.OllamaTimeoutMinutesOverride ?? ContentStudioOptions.DefaultGenerationTimeoutMinutes,
            ContentStudioOptions.DefaultGenerationTimeoutMinutes);
        return TimeSpan.FromMinutes(limit) + StuckGrace;
    }

    private async Task<bool> AnswersAsync(CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(ollamaOptions.Value.BaseUrl) ? ContentStudioOptions.DefaultBaseUrl : ollamaOptions.Value.BaseUrl;
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .GetAsync(new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "api/version"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
