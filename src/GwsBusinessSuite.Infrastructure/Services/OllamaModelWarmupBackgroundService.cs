using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

/// <summary>
/// Warms the configured chat model after startup without delaying application readiness.
/// The Ollama container reports healthy before first-run model setup is necessarily complete,
/// so the warmup retries until the selected model appears or the bounded window expires.
/// </summary>
public sealed class OllamaModelWarmupBackgroundService(
    IServiceScopeFactory scopeFactory,
    OllamaWorkloadScheduler workloads,
    ILogger<OllamaModelWarmupBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private const int MaxAttempts = 12;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);
        using var workloadPriority = workloads.UseBackgroundPriority();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var settings = await scope.ServiceProvider.GetRequiredService<ISiteSettingsService>()
                    .GetSettingsAsync(stoppingToken);
                var model = string.IsNullOrWhiteSpace(settings.OllamaModelOverride)
                    ? SentinelGptDefaults.Model
                    : settings.OllamaModelOverride;
                var ollama = scope.ServiceProvider.GetRequiredService<IOllamaService>();
                var installed = await ollama.ListModelsAsync(stoppingToken);
                if (!installed.Any(item => ModelNamesMatch(item, model)))
                {
                    logger.LogInformation(
                        "Ollama warmup attempt {Attempt}/{MaxAttempts}: model '{Model}' is not installed yet.",
                        attempt,
                        MaxAttempts,
                        model);
                }
                else
                {
                    await ollama.WarmModelAsync(model, stoppingToken);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Ollama warmup attempt {Attempt}/{MaxAttempts} failed.",
                    attempt,
                    MaxAttempts);
            }

            if (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }

        logger.LogWarning("Ollama model warmup did not complete during the startup retry window.");
    }

    internal static bool ModelNamesMatch(string installed, string configured) =>
        string.Equals(NormalizeModelName(installed), NormalizeModelName(configured), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeModelName(string model) =>
        model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? model[..^":latest".Length]
            : model;
}
