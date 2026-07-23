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
