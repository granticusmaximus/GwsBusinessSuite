using System.Reflection;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Web.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GwsBusinessSuite.Tests;

// 19-11-26: a hung Ollama on the AI droplet made /health/ready 503, so a deploy and its rollback
// both failed. The site works without AI, so Ollama trouble is Degraded (ready answers 200).
public sealed class OllamaHealthCheckTests
{
    [Fact]
    public async Task HungOllama_IsDegraded_NotUnhealthy()
    {
        var check = new OllamaHealthCheck(Ollama(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("did not respond");
    }

    [Fact]
    public async Task UnreachableOllama_IsDegraded_AndAWorkingOneHealthy()
    {
        var down = new OllamaHealthCheck(Ollama(_ => throw new HttpRequestException("Connection refused")));
        var up = new OllamaHealthCheck(Ollama(_ => Task.FromResult<IReadOnlyCollection<string>>(["sentinelgpt"])));

        (await down.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded);
        (await up.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);
    }

    private static IOllamaService Ollama(Func<CancellationToken, Task<IReadOnlyCollection<string>>> listModels)
    {
        var proxy = DispatchProxy.Create<IOllamaService, OllamaProxy>();
        ((OllamaProxy)(object)proxy).ListModels = listModels;
        return proxy;
    }

    public class OllamaProxy : DispatchProxy
    {
        public Func<CancellationToken, Task<IReadOnlyCollection<string>>> ListModels { get; set; } = _ => Task.FromResult<IReadOnlyCollection<string>>([]);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IOllamaService.ListModelsAsync)
                ? ListModels(args is [CancellationToken ct, ..] ? ct : default)
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
