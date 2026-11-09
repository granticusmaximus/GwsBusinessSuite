using FluentAssertions;

namespace GwsBusinessSuite.Tests;

public sealed class ClientProductionEndpointTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void NativeClients_ShouldDefaultToTheCertificateBackedAppOrigin()
    {
        var appEndpoints = File.ReadAllText(Path.Combine(
            RepoRoot, "src/GwsBusinessSuite.App/AppEndpoints.cs"));
        var menuBar = File.ReadAllText(Path.Combine(
            RepoRoot, "src/GwsBusinessSuite.SentinelMenuBar/AppDelegate.cs"));

        appEndpoints.Should().Contain("DefaultAppHost = \"https://admin.gwsapp.net\"");
        menuBar.Should().Contain("return \"https://admin.gwsapp.net\"");
        appEndpoints.Should().NotContain("DefaultAppHost = \"https://gws-pooldp\"");
        menuBar.Should().NotContain("return \"https://gws-pooldp\"");

        foreach (var environmentVariable in new[] { "GWS_APP_BASE_URL", "GWS_BASE_URL", "GWS_HOST_URL" })
        {
            appEndpoints.Should().Contain(environmentVariable);
            menuBar.Should().Contain(environmentVariable);
        }
    }
}
