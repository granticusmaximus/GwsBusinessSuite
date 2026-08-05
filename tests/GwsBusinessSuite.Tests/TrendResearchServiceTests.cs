using System.Net;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class TrendResearchServiceTests
{
    // Regression guard for a real finding: a specific focus area (e.g. "blazor server
    // hosting") requires an exact Hacker News keyword match within the last 7 days AND an
    // exact single-word dev.to tag - verified against the live APIs, most realistic
    // multi-word focus areas hit zero results on both and the feature always reported
    // "No live trend data could be retrieved" even though the community signal exists, just
    // outside those narrow filters. The fix widens the HN window and falls back to dev.to's
    // general feed when the strict pass comes back empty - this exercises exactly that: the
    // recency-filtered HN query and the tag-filtered dev.to query both return zero results,
    // and only the wider/fallback queries return data.
    [Fact]
    public async Task ResearchTrendsAsync_ShouldFallBackToWiderQueries_WhenStrictFocusAreaMatchIsEmpty()
    {
        var handler = new RecordingHandler(request =>
        {
            // AbsoluteUri (not ToString()) - ToString() unescapes reserved characters like the
            // "%3E" in numericFilters=created_at_i%3E..., which would make the marker below
            // never match and silently misclassify every HN call as the narrow one.
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("hn.algolia.com"))
            {
                var narrowWindow = !url.Contains("created_at_i%3E") || IsWithinLast(url, TimeSpan.FromDays(8));
                var hits = narrowWindow
                    ? "[]"
                    : "[{\"title\":\"Older but relevant story\",\"url\":\"https://example.com/a\",\"points\":42,\"num_comments\":3,\"objectID\":\"1\"}]";
                return JsonResponse("{\"hits\": " + hits + " }");
            }

            if (url.Contains("dev.to"))
            {
                var hasTag = url.Contains("&tag=");
                return hasTag
                    ? JsonResponse("[]")
                    : JsonResponse("""[{"title":"General community post","url":"https://dev.to/example","public_reactions_count":10,"comments_count":2}]""");
            }

            throw new InvalidOperationException($"Unexpected request: {url}");
        });

        var service = new TrendResearchService(
            new HttpClient(handler),
            new StubOllamaService(),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ContentStudioOptions()),
            NullLogger<TrendResearchService>.Instance);

        var result = await service.ResearchTrendsAsync(new TrendResearchRequest { FocusArea = "blazor server hosting" });

        Assert.NotEmpty(result.Signals);
        Assert.Contains(result.Signals, signal => signal.Source == "Hacker News");
        Assert.Contains(result.Signals, signal => signal.Source == "dev.to");
        Assert.DoesNotContain("No live trend data could be retrieved", result.OverallSummary);
    }

    private static bool IsWithinLast(string url, TimeSpan window)
    {
        var marker = "created_at_i%3E";
        var index = url.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return false;
        var start = index + marker.Length;
        var end = start;
        while (end < url.Length && char.IsDigit(url[end])) end++;
        var epochSeconds = long.Parse(url[start..end]);
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        return cutoff >= DateTimeOffset.UtcNow.Subtract(window);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class StubOllamaService : IOllamaService
    {
        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            Task.FromResult("OVERALL_SUMMARY: Stubbed summary for the test.");

        public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }

    [Fact]
    public void ParseOllamaResponse_ShouldExtractSummaryAndSuggestions_FromWellFormedResponse()
    {
        const string raw = """
            OVERALL_SUMMARY: Developers are discussing Blazor state management and AI tooling.
            ---
            TOPIC: Blazor state management patterns
            PRIMARY_KEYWORD: Blazor state management
            SECONDARY_KEYWORDS: Razor Components, scalability
            RATIONALE: Several trending posts cover scaling Blazor apps.
            POSITIVE_TAKE: Good state management improves performance.
            NEGATIVE_TAKE: Poor state management causes bugs.
            ---
            TOPIC: Azure AD auth in Blazor WebAssembly
            PRIMARY_KEYWORD: Blazor Azure AD authentication
            SECONDARY_KEYWORDS: security, role-based access control
            RATIONALE: A trending post covers RBAC with Azure AD.
            POSITIVE_TAKE: Azure AD provides robust security.
            NEGATIVE_TAKE: Added complexity and cost.
            """;

        var (summary, suggestions) = TrendResearchService.ParseOllamaResponse(raw);

        Assert.Contains("Blazor state management", summary);
        Assert.Equal(2, suggestions.Count);
        Assert.Equal("Blazor state management patterns", suggestions[0].Topic);
        Assert.Equal("Blazor state management", suggestions[0].PrimaryKeyword);
        Assert.Equal("Azure AD auth in Blazor WebAssembly", suggestions[1].Topic);
    }

    [Fact]
    public void ParseOllamaResponse_ShouldSkipBlocksMissingTopic()
    {
        const string raw = """
            OVERALL_SUMMARY: Short summary.
            ---
            RATIONALE: This block has no TOPIC line and should be skipped.
            ---
            TOPIC: Valid suggestion
            PRIMARY_KEYWORD: valid keyword
            """;

        var (_, suggestions) = TrendResearchService.ParseOllamaResponse(raw);

        Assert.Single(suggestions);
        Assert.Equal("Valid suggestion", suggestions[0].Topic);
    }

    [Fact]
    public void ParseOllamaResponse_ShouldFallBackToRawText_WhenNoSummaryLabelPresent()
    {
        const string raw = "The model ignored the requested format entirely.";

        var (summary, suggestions) = TrendResearchService.ParseOllamaResponse(raw);

        Assert.Equal(raw, summary);
        Assert.Empty(suggestions);
    }
}