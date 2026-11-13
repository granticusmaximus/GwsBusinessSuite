using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

// SentinelGPT's key-less web search (13-11-26): SearXNG when there's no Ollama web key.
public sealed class KeylessWebSearchTests
{
    [Fact]
    public async Task WithASearxngUrlAndNoKey_ShouldSearchSearxng_KeepingOnlyPublicHttpsResults()
    {
        Uri? asked = null;
        var http = new HttpClient(new Handler(request =>
        {
            asked = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"results":[
                      {"title":"Quake hits","url":"https://news.example/quake","content":"A magnitude 6"},
                      {"title":"Same","url":"https://news.example/quake","content":"duplicate"},
                      {"title":"Plain http","url":"http://insecure.example/","content":"x"},
                      {"title":"Private","url":"https://10.0.0.5/","content":"x"}
                    ]}
                    """, System.Text.Encoding.UTF8, "application/json"),
            };
        }));
        var service = new OllamaWebSearchService(http, Options.Create(new OllamaWebOptions { SearxngUrl = "http://10.1.2.3:8888/" }));

        service.IsConfigured.Should().BeTrue();
        var results = await service.SearchAsync("earthquake today");

        asked!.AbsoluteUri.Should().StartWith("http://10.1.2.3:8888/search?format=json").And.Contain("q=earthquake%20today");
        results.Should().ContainSingle().Which.Should().Be(new GwsBusinessSuite.Application.Abstractions.OllamaWebSearchResult("Quake hits", "https://news.example/quake", "A magnitude 6"));
    }

    [Fact]
    public void WithNeitherKeyNorSearxng_ShouldNotBeConfigured() =>
        new OllamaWebSearchService(new HttpClient(), Options.Create(new OllamaWebOptions())).IsConfigured.Should().BeFalse();

    [Fact]
    public void PageText_ShouldKeepTheArticleAndDropScriptsAndChrome()
    {
        var (title, text) = OllamaWebSearchService.PageText("""
            <html><head><title>Flood in Izmir</title><script>var x=1;</script></head>
            <body><nav>Home | World</nav><article><h1>Flood in Izmir</h1><p>Rescuers reached 40 people.</p>
            <script>track()</script><p>Rain continues.</p></article><footer>© News</footer></body></html>
            """);

        title.Should().Be("Flood in Izmir");
        text.Should().Contain("Rescuers reached 40 people.").And.Contain("Rain continues.");
        text.Should().NotContain("track()").And.NotContain("Home | World").And.NotContain("© News");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
