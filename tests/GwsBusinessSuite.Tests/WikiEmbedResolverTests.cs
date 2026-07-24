using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class WikiEmbedResolverTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "YouTube", "https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "YouTube", "https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://vimeo.com/76979871", "Vimeo", "https://player.vimeo.com/video/76979871")]
    [InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT", "Spotify", "https://open.spotify.com/embed/track/4cOdK2wGLETKBW3PvgPWqT")]
    [InlineData("https://codepen.io/someone/pen/abcXYZ", "CodePen", "https://codepen.io/someone/embed/abcXYZ?default-tab=result")]
    [InlineData("https://www.loom.com/share/abc123def456", "Loom", "https://www.loom.com/embed/abc123def456")]
    public void TryResolve_ShouldRecognizeKnownProviders(string url, string expectedProvider, string expectedEmbedUrl)
    {
        WikiEmbedResolver.TryResolve(url, out var embedUrl, out var providerLabel).Should().BeTrue();
        providerLabel.Should().Be(expectedProvider);
        embedUrl.Should().Be(expectedEmbedUrl);
    }

    [Fact]
    public void TryResolve_ShouldPassTheFullOriginalUrlThroughForFigma()
    {
        const string url = "https://www.figma.com/file/abc123/My-Design?node-id=1%3A2";

        WikiEmbedResolver.TryResolve(url, out var embedUrl, out var providerLabel).Should().BeTrue();

        providerLabel.Should().Be("Figma");
        embedUrl.Should().Contain("embed_host=sentinel");
        embedUrl.Should().Contain(Uri.EscapeDataString(url));
    }

    [Theory]
    [InlineData("https://example.com/some-page")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolve_ShouldFallBackForUnrecognizedOrEmptyUrls(string? url)
    {
        WikiEmbedResolver.TryResolve(url, out var embedUrl, out var providerLabel).Should().BeFalse();
        embedUrl.Should().BeEmpty();
        providerLabel.Should().BeEmpty();
    }
}
