using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class TrendResearchServiceTests
{
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