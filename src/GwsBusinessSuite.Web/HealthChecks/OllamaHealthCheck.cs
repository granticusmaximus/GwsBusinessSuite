using GwsBusinessSuite.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GwsBusinessSuite.Web.HealthChecks;

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
            return HealthCheckResult.Unhealthy("Ollama did not respond within the health check timeout.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Ollama is unreachable.", ex);
        }
    }
}
