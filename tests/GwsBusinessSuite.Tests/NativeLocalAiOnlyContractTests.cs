using FluentAssertions;

namespace GwsBusinessSuite.Tests;

public sealed class NativeLocalAiOnlyContractTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void NativeSentinelGpt_ShouldHaveNoServerInferenceFallback()
    {
        var page = Source("src/GwsBusinessSuite.App/SentinelGptPage.xaml.cs");
        var program = Source("src/GwsBusinessSuite.Web/Program.cs");
        var registrations = Source("src/GwsBusinessSuite.App/MauiProgram.cs");

        page.Should().Contain("The macOS app never sends AI work to the AI droplet.");
        page.Should().NotContain("NativeFallbackChatService");
        registrations.Should().NotContain("NativeFallbackChatService");
        program.Should().NotContain("/native/fallback-chat");
        program.Should().NotContain("native-fallback-chat");
    }

    [Fact]
    public void NativeAgentTurn_ShouldRemainBoundToTheSignedInUsersMacRelay()
    {
        var turns = Source("src/GwsBusinessSuite.Application/SentinelAgent/SentinelAgentTurns.cs");

        turns.Should().Contain("new RelayedOllamaService(relay, user.Username)");
        turns.Should().NotContain("FailoverOllamaService");
    }

    [Fact]
    public void NativeApp_ShouldAllowNormalIdleSystemSleep()
    {
        var server = Source("src/GwsBusinessSuite.App/Platforms/MacCatalyst/MacAiServer.cs");
        var app = Source("src/GwsBusinessSuite.App/App.xaml.cs");

        server.Should().Contain("UserInitiated & ~NSActivityOptions.IdleSystemSleepDisabled",
            "the relay may avoid App Nap while the Mac is awake but must explicitly allow idle system sleep");
        server.Should().NotContain("UserInitiated | NSActivityOptions.IdleSystemSleepDisabled");
        server.Should().NotContain("SetLocalAiAvailable");
        app.Should().NotContain("LocalAiPowerPolicyService");
    }

    private static string Source(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
