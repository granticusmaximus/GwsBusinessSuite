using FluentAssertions;
using GwsBusinessSuite.Application.AffiliateAnalytics;

namespace GwsBusinessSuite.Tests;

public sealed class AffiliateClickFilterTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Safari/605.1.15")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15")]
    public void Classify_ShouldPass_ARealBrowserUserAgent(string userAgent)
    {
        AffiliateClickFilter.Classify(userAgent, null).Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
    [InlineData("Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)")]
    [InlineData("Mozilla/5.0 (compatible; SemrushBot/7~bl)")]
    [InlineData("curl/8.4.0")]
    [InlineData("python-requests/2.31.0")]
    [InlineData("Go-http-client/1.1")]
    [InlineData("GPTBot/1.0")]
    [InlineData("ClaudeBot/1.0")]
    public void Classify_ShouldFilter_KnownBotUserAgents(string userAgent)
    {
        var result = AffiliateClickFilter.Classify(userAgent, null);
        result.Passed.Should().BeFalse();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Classify_ShouldFilter_AMissingUserAgent()
    {
        var result = AffiliateClickFilter.Classify(null, null);
        result.Passed.Should().BeFalse();
        result.Reason.Should().Be("Missing user-agent");
    }

    [Fact]
    public void Classify_ShouldFilter_APrefetchRequest_EvenWithARealBrowserUserAgent()
    {
        var result = AffiliateClickFilter.Classify(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0",
            "prefetch");

        result.Passed.Should().BeFalse();
        result.Reason.Should().Be("Prefetch request");
    }
}
