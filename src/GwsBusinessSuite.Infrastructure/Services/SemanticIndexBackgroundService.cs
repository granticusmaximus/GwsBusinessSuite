using GwsBusinessSuite.Application.SemanticSearch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SemanticIndexBackgroundService(
    SemanticIndexQueue queue,
    IServiceScopeFactory scopeFactory,
    OllamaWorkloadScheduler workloads,
    IOptions<SemanticSearchOptions> options,
    ILogger<SemanticIndexBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Semantic indexing is disabled by configuration.");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            queue.RequestReconciliation();
            var reconciliationInterval = TimeSpan.FromMinutes(
                Math.Clamp(options.Value.ReconciliationMinutes, 1, 1440));

            while (!stoppingToken.IsCancellationRequested)
            {
                await queue.WaitAsync(reconciliationInterval, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

                try
                {
                    using var priority = workloads.UseBackgroundPriority();
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IHybridSearchService>()
                        .RebuildAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Search remains keyword-capable when Ollama/the embedding model is absent.
                    // Reconciliation retries on the next save or timer tick without taking down
                    // the app or poisoning the source transaction.
                    logger.LogWarning(ex, "Semantic index reconciliation failed; keyword retrieval remains available.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
