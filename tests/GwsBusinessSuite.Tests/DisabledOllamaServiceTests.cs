using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Infrastructure;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GwsBusinessSuite.Tests;

public sealed class DisabledOllamaServiceTests
{
    [Fact]
    public async Task Calls_ShouldFailWithMacAppDirection_WithoutNetworkAccess()
    {
        var service = new DisabledOllamaService();

        var generate = () => service.GenerateAsync("model", "system", "prompt");
        var embed = () => service.EmbedAsync("embeddinggemma", ["headline"]);

        (await generate.Should().ThrowAsync<ServerAiUnavailableException>())
            .Which.Message.Should().Contain("Mac app");
        await embed.Should().ThrowAsync<ServerAiUnavailableException>();
        (await service.ListModelsAsync()).Should().BeEmpty();
    }

    [Fact]
    public void AddInfrastructure_InNoneMode_ShouldOmitOllamaHostedServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ServerAiOptions.SectionName}:Mode"] = nameof(ServerAiMode.None),
                ["DataProtection:KeysPath"] = Path.Combine(Path.GetTempPath(), "gws-disabled-ai-tests")
            })
            .Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IOllamaService)
            && descriptor.ImplementationType == typeof(DisabledOllamaService));
        services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .Should().NotContain([
                typeof(OllamaModelWarmupBackgroundService),
                typeof(OllamaWatchdogBackgroundService),
                typeof(SemanticIndexBackgroundService)
            ]);
    }
}
