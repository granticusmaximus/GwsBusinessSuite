using FluentAssertions;

namespace GwsBusinessSuite.Tests;

public sealed class NativeWorkspaceNavigationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void OrdinaryNavigation_ShouldRemainNonBlocking()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.App/MainPage.xaml.cs"));
        var navigating = Slice(source, "private void OnWorkspaceNavigating", "private async Task HandleNativeRequestAsync");

        navigating.Should().Contain("NavigationProgress.IsVisible = !LoadingOverlay.IsVisible;");
        navigating.Should().Contain("StartNavigationTimeout(blockOnTimeout: LoadingOverlay.IsVisible);");
        navigating.Should().NotContain("LoadingOverlay.IsVisible = true;");
    }

    [Fact]
    public void SlowOrdinaryNavigation_ShouldNotShowFullScreenFailure()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.App/MainPage.xaml.cs"));
        var timeout = Slice(source, "private void StartNavigationTimeout", "private void StopNavigationTimeout");

        timeout.Should().Contain("if (blockOnTimeout)");
        timeout.Should().Contain("SlowNavigationNotice.IsVisible = true;");
    }

    [Fact]
    public void ProgressIndicator_ShouldNotCaptureInput()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.App/MainPage.xaml"));
        var progress = Slice(xaml, "<BoxView x:Name=\"NavigationProgress\"", "/>");

        progress.Should().Contain("InputTransparent=\"True\"");
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        startIndex.Should().BeGreaterThanOrEqualTo(0);
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        endIndex.Should().BeGreaterThan(startIndex);
        return source[startIndex..endIndex];
    }
}
