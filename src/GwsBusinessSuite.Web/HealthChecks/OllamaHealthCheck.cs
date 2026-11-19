using GwsBusinessSuite.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GwsBusinessSuite.Web.HealthChecks;

// Degraded, never Unhealthy (19-11-26): the site works without AI, but a hung Ollama on the AI
// droplet made /health/ready 503, so a deploy and then its rollback both failed and the live site
// was marked not ready. AI features already say when the server model is unavailable.
public sealed class OllamaHealthCheck(IOllamaService ollamaService) : IHealthCheck
{
    // The configured Ollama HttpClient timeout is set for long generation calls (minutes),
    // but listing models is just metadata and should return almost instantly. Cap this
    // check separately so an unresponsive Ollama doesn't hang the health endpoint.
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CheckTimeout);

        try
        {
            var models = await ollamaService.ListModelsAsync(timeoutCts.Token);

            return models.Count > 0
                ? HealthCheckResult.Healthy($"Ollama is reachable with {models.Count} model(s) available.")
                : HealthCheckResult.Degraded("Ollama is reachable but reported zero models.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded("Ollama did not respond within the health check timeout.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Ollama is unreachable.", ex);
        }
    }
}
