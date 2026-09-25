using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class DefendNetworkFeedServiceTests
{
    // Trimmed but structurally real - mirrors defend.network/feed.xml's actual shape (confirmed
    // via a direct fetch during implementation): a bare <category> for severity, domain-
    // attributed <category> elements for threat-type/industry tags.
    private const string SampleRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom" xmlns:dc="http://purl.org/dc/elements/1.1/">
        <channel>
          <title>defend.network - Daily Threat Briefings</title>
          <link>https://defend.network</link>
          <description>Threat briefings</description>
          <item>
            <title>Sample critical exploit briefing</title>
            <link>https://defend.network/briefings/sample-2026-09-25.html</link>
            <guid isPermaLink="true">https://defend.network/briefings/sample-2026-09-25.html</guid>
            <pubDate>Fri, 25 Sep 2026 06:30:00 GMT</pubDate>
            <dc:creator>defend.network</dc:creator>
            <description>A sample vulnerability actively exploited in the wild.</description>
            <category>high</category>
            <category domain="https://defend.network/threats/">vulnerability-exploit</category>
            <category domain="https://defend.network/industries/">technology</category>
          </item>
        </channel>
        </rss>
        """;

    [Fact]
    public async Task GetRecentBriefingsAsync_ShouldMapABriefing_SplittingSeverityFromTags()
    {
        var service = CreateService(_ => RssResponse(SampleRss));

        var result = await service.GetRecentBriefingsAsync();

        result.Should().ContainSingle();
        var briefing = result[0];
        briefing.Title.Should().Be("Sample critical exploit briefing");
        briefing.Url.Should().Be("https://defend.network/briefings/sample-2026-09-25.html");
        briefing.Description.Should().Be("A sample vulnerability actively exploited in the wild.");
        briefing.Severity.Should().Be("high");
        briefing.Tags.Should().BeEquivalentTo(["vulnerability-exploit", "technology"]);
        briefing.PublishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRecentBriefingsAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheFeedFails()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await service.GetRecentBriefingsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecentBriefingsAsync_ShouldCacheAcrossCalls_WithoutRefetching()
    {
        var callCount = 0;
        var service = CreateService(_ =>
        {
            callCount++;
            return RssResponse(SampleRss);
        });

        await service.GetRecentBriefingsAsync();
        await service.GetRecentBriefingsAsync();

        callCount.Should().Be(1);
    }

    private static HttpResponseMessage RssResponse(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml")
    };

    private static DefendNetworkFeedService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var handler = new RecordingHandler(responseFactory);
        var factory = new FakeHttpClientFactory(handler);
        return new DefendNetworkFeedService(factory, new MemoryCache(new MemoryCacheOptions()), NullLogger<DefendNetworkFeedService>.Instance);
    }

    // Matches OverpassBusinessInfoServiceTests' own precedent: real IHttpClientFactory.CreateClient()
    // returns a fresh, independently-disposable HttpClient wrapper per call over a shared handler.
    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
