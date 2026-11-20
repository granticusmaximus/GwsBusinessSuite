using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// 20-11-26 gap sweep: how the server's HTTP clients behave.
public sealed class HttpClientConductTests
{
    private static readonly string Di = File.ReadAllText(Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")), "src/GwsBusinessSuite.Infrastructure/DependencyInjection.cs"));

    // AGENTS.md: never get around bot protection with a browser user agent (Eventbrite's client did).
    [Fact]
    public void NoClient_ShouldPoseAsABrowser()
    {
        Di.Should().NotContain("Chrome/").And.NotContain("Safari/537").And.NotContain("Intel Mac OS X 10_15_7) AppleWebKit");
    }

    // UN News sent its feed compressed and World News read gzip bytes as XML.
    [Fact]
    public void WorldNewsClient_ShouldDecompressFeeds()
    {
        var start = Di.IndexOf("services.AddHttpClient(WorldNewsService.HttpClientName", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        Di.Substring(start, 900).Should().Contain("AutomaticDecompression = System.Net.DecompressionMethods.All");
    }
}
