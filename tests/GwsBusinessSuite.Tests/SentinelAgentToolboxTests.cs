using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GwsBusinessSuite.Tests;

// Every agent turn resolves every toolset (SentinelAgentRunner.ToolsFor), so one that can't be
// built from the app's real registrations, or two tools with one name, would break every turn.
public sealed class SentinelAgentToolboxTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryToolset_ShouldResolveFromTheAppsContainer_WithUniqueToolNames()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gws-agent-toolbox", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
                // Read by the research tools' World News, which opens its own store.
                ["WorldNews:DatabasePath"] = Path.Combine(folder, "worldnews.db")
            })
            .Build();
        var services = new ServiceCollection();
        // What the web host provides, then the app's own registrations.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInfrastructure(configuration);
        // Program.cs registers these two (the accessor is the Web project's).
        services.AddScoped<ActingUser>();
        services.AddScoped(_ => FixedCurrentUserAccessor.Unknown);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var tools = scope.ServiceProvider.GetServices<ISentinelAgentToolset>().SelectMany(toolset => toolset.Tools).ToList();

        tools.Select(tool => tool.Name).Should().OnlyHaveUniqueItems()
            .And.Contain(["create_todo_list", "add_card", "create_site_page_draft", "find_contacts", "create_automation",
                "update_ticket", "log_time", "create_blog_post_draft", "find_media", "send_email", "change_setting", "overwatch_briefing"]);
        var definitionCharacters = tools.Sum(tool => tool.Name.Length + tool.Description.Length + tool.ParametersJson.Length);
        output.WriteLine($"{tools.Count} tools, {definitionCharacters} characters of definitions.");
        scope.Dispose();
        provider.Dispose();
        Directory.Delete(folder, recursive: true);
    }
}
