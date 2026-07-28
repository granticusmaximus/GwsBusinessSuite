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
            Options.Create(new OllamaWebOptions { ApiKey = "server-only-key", MaxResults = 4 }));

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
            Options.Create(new OllamaWebOptions { ApiKey = "server-only-key" }));

        var act = () => service.FetchAsync(url);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*public HTTPS*");
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
