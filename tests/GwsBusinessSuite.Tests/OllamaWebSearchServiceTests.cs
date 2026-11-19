using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class OllamaWebSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_ShouldSendTheServerSideKeyAndDiscardUnsafeResultUrls()
    {
        var handler = new RecordingHandler("""
            {
              "results": [
                {
                  "title": "Official docs",
                  "url": "https://docs.ollama.com/",
                  "content": "Documentation"
                },
                {
                  "title": "Local service",
                  "url": "http://127.0.0.1:11434/api/tags",
                  "content": "Should be discarded"
                }
              ]
            }
            """);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://ollama.com") };
        var service = new OllamaWebSearchService(
            http,
            Options.Create(new OllamaWebOptions { ApiKey = "server-only-key", AllowPaidSearch = true, MaxResults = 4 }));

        var results = await service.SearchAsync("Ollama documentation");

        results.Should().ContainSingle().Which.Url.Should().Be("https://docs.ollama.com/");
        handler.Authorization.Should().Be("Bearer server-only-key");
        handler.RequestBody.Should().Contain("\"max_results\":4");
    }

    [Fact]
    public async Task SearchAsync_ShouldFailClearlyWhenTheKeyIsMissing()
    {
        var service = new OllamaWebSearchService(
            new HttpClient(new RecordingHandler("{}")) { BaseAddress = new Uri("https://ollama.com") },
            Options.Create(new OllamaWebOptions()));

        var act = () => service.SearchAsync("test query");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*OllamaWeb__ApiKey*");
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://localhost/admin")]
    [InlineData("https://127.0.0.1/private")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public async Task FetchAsync_ShouldRejectNonPublicHttpsUrls(string url)
    {
        var service = new OllamaWebSearchService(
            new HttpClient(new RecordingHandler("{}")) { BaseAddress = new Uri("https://ollama.com") },
            Options.Create(new OllamaWebOptions { ApiKey = "server-only-key", AllowPaidSearch = true }));

        var act = () => service.FetchAsync(url);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*public HTTPS*");
    }

    // 19-11-26: production's Ollama key answered 402 to every World News discovery search.
    [Fact]
    public async Task Search_ShouldHandOverToSearxng_WhileOllamaRefusesTheKey()
    {
        var asked = new List<(string Host, string? Authorization)>();
        var http = new HttpClient(new AnsweringHandler(request =>
        {
            asked.Add((request.RequestUri!.Host, request.Headers.Authorization?.ToString()));
            return request.RequestUri.Host == "ollama.com"
                ? new HttpResponseMessage(HttpStatusCode.PaymentRequired)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"results":[{"title":"Story","url":"https://news.example/story","content":"Text"}]}""",
                        Encoding.UTF8, "application/json"),
                };
        })) { BaseAddress = new Uri("https://ollama.com") };
        var service = new OllamaWebSearchService(http, Options.Create(new OllamaWebOptions
        {
            ApiKey = $"out-of-credit-{Guid.NewGuid():N}",
            AllowPaidSearch = true,
            SearxngUrl = "http://searx.internal:8080",
        }));

        (await service.SearchRecentAsync("breaking world news today")).Should().ContainSingle()
            .Which.Url.Should().Be("https://news.example/story");
        (await service.SearchAsync("second search")).Should().ContainSingle();

        asked.Select(a => a.Host).Should().Equal(["ollama.com", "searx.internal", "searx.internal"],
            "after a 402 Ollama is left alone for a while rather than asked every search");
        asked.Where(a => a.Host == "searx.internal").Should().OnlyContain(a => a.Authorization == null,
            "the Ollama key is never sent to SearXNG");
        OllamaWebSearchService.PauseAfter(HttpStatusCode.TooManyRequests).Should().Be(TimeSpan.FromMinutes(15));
        OllamaWebSearchService.PauseAfter(HttpStatusCode.InternalServerError).Should().BeNull("an outage isn't a refusal of the key");
    }

    [Fact]
    public async Task Search_ShouldStillFail_WhenOllamaRefusesAndThereIsNoSearxng()
    {
        var http = new HttpClient(new AnsweringHandler(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired)))
            { BaseAddress = new Uri("https://ollama.com") };
        var service = new OllamaWebSearchService(http, Options.Create(new OllamaWebOptions { ApiKey = $"k-{Guid.NewGuid():N}", AllowPaidSearch = true }));

        await service.Invoking(s => s.SearchAsync("anything")).Should().ThrowAsync<HttpRequestException>();
    }

    // 19-11-26, Grant: no Ollama charges. A key in .env is ignored unless paid search is allowed.
    [Fact]
    public async Task Search_ShouldNeverUseThePaidApi_UnlessAllowedOnPurpose()
    {
        var asked = new List<string>();
        var http = new HttpClient(new AnsweringHandler(request =>
        {
            asked.Add(request.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[{"title":"Story","url":"https://news.example/story","content":"Text"}]}""",
                    Encoding.UTF8, "application/json"),
            };
        })) { BaseAddress = new Uri("https://ollama.com") };
        var withSearxng = new OllamaWebSearchService(http, Options.Create(new OllamaWebOptions
        {
            ApiKey = "a-key-left-in-env",
            SearxngUrl = "http://searx.internal:8080",
        }));
        var keyOnly = new OllamaWebSearchService(http, Options.Create(new OllamaWebOptions { ApiKey = "a-key-left-in-env" }));

        (await withSearxng.SearchAsync("world news")).Should().ContainSingle();

        asked.Should().Equal(["searx.internal"]);
        keyOnly.IsConfigured.Should().BeFalse("a key alone never turns the billed search on");
    }

    private sealed class AnsweringHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
