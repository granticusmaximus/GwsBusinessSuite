using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class NotionServiceTests
{
    [Fact]
    public async Task GetPageMarkdownAsync_ShouldRequestFullContentAndTranscript()
    {
        var handler = new RecordingHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.PathAndQuery.Should().Be(
                "/v1/pages/page-1/markdown?include_transcript=true");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("secret");
            request.Headers.GetValues("Notion-Version").Should().ContainSingle()
                .Which.Should().Be(NotionService.NotionVersion);
            return JsonResponse(
                """
                {
                  "object":"page_markdown",
                  "id":"page-1",
                  "markdown":"# Real content",
                  "truncated":true,
                  "unknown_block_ids":["unknown-1"]
                }
                """);
        });
        var service = CreateService(handler);

        var page = await service.GetPageMarkdownAsync("secret", "page-1");

        page.Should().NotBeNull();
        page!.Markdown.Should().Be("# Real content");
        page.Truncated.Should().BeTrue();
        page.UnknownBlockIds.Should().Equal("unknown-1");
    }

    [Fact]
    public async Task GetBlockChildrenAsync_ShouldPreserveNotion404Details()
    {
        var service = CreateService(new RecordingHandler(_ =>
            JsonResponse(
                """{"object":"error","code":"object_not_found","message":"Could not find block."}""",
                HttpStatusCode.NotFound)));

        var action = () => service.GetBlockChildrenAsync("secret", "missing-page", null);

        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        exception.Which.Message.Should().Contain("retrieving block children for missing-page");
        exception.Which.Message.Should().Contain("Could not find block");
    }

    [Fact]
    public async Task SearchAsync_ShouldRetryRateLimitedRequestsAndPreserveThePostBody()
    {
        var requests = new List<string>();
        var attempts = 0;
        var handler = new RecordingHandler(request =>
        {
            attempts++;
            requests.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            if (attempts == 1)
            {
                var limited = JsonResponse(
                    """{"object":"error","code":"rate_limited","message":"Slow down."}""",
                    HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }

            return JsonResponse(
                """{"object":"list","results":[{"object":"page","id":"page-1"}],"has_more":false,"next_cursor":null}""");
        });
        var service = CreateService(handler);

        var result = await service.SearchAsync("secret", null);

        attempts.Should().Be(2);
        requests.Should().HaveCount(2);
        requests.Should().OnlyContain(body => body.Contains("\"page_size\":100", StringComparison.Ordinal));
        result.Results.Should().ContainSingle();
        result.Results[0].GetProperty("id").GetString().Should().Be("page-1");
    }

    [Fact]
    public async Task SearchAsync_ShouldStopAfterBoundedRateLimitRetries()
    {
        var attempts = 0;
        var handler = new RecordingHandler(_ =>
        {
            attempts++;
            var limited = JsonResponse(
                """{"object":"error","code":"rate_limited","message":"Still limited."}""",
                HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return limited;
        });
        var service = CreateService(handler);

        var action = () => service.SearchAsync("secret", null);

        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(NotionService.MaxRateLimitAttempts);
        exception.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        exception.Which.Message.Should().Contain("Still limited");
    }

    private static NotionService CreateService(HttpMessageHandler handler) =>
        new(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.notion.test/v1/")
        });

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
