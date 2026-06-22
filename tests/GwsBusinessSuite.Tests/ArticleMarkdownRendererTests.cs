using GwsBusinessSuite.Application.Articles;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

public sealed class ArticleMarkdownRendererTests
{
    [Fact]
    public void Render_ShouldReplaceSlotTokenWithAdCardMarkup()
    {
        var placement = new ArticleAffiliatePlacement
        {
            SlotToken = "{{CJ_AD_abc12345}}",
            AdvertiserName = "Acme Tools",
            Category = "Developer Tools",
            TrackingUrl = "https://example.com/track",
            CallToActionText = "Check it out"
        };

        var rendered = ArticleMarkdownRenderer.Render(
            "Intro text.\n\n{{CJ_AD_abc12345}}\n\nMore text.",
            [placement]);

        Assert.Contains("Acme Tools", rendered);
        Assert.Contains("https://example.com/track", rendered);
        Assert.Contains("Check it out", rendered);
        Assert.DoesNotContain("{{CJ_AD_abc12345}}", rendered);
    }

    [Fact]
    public void Render_ShouldStripOrphanTokens_WhenNoMatchingPlacementExists()
    {
        var rendered = ArticleMarkdownRenderer.Render(
            "Before.\n\n{{CJ_AD_deadbeef}}\n\nAfter.",
            []);

        Assert.DoesNotContain("{{CJ_AD_deadbeef}}", rendered);
        Assert.Contains("Before.", rendered);
        Assert.Contains("After.", rendered);
    }

    [Fact]
    public void Render_ShouldFallBackToGenericCategoryAndUrl_WhenMissing()
    {
        var placement = new ArticleAffiliatePlacement
        {
            SlotToken = "{{CJ_AD_xyz}}",
            AdvertiserName = "Acme Tools",
            Category = string.Empty,
            TrackingUrl = string.Empty,
            CallToActionText = "Explore Offer"
        };

        var rendered = ArticleMarkdownRenderer.Render("{{CJ_AD_xyz}}", [placement]);

        Assert.Contains("Category: General", rendered);
        Assert.Contains("href=\"#\"", rendered);
    }

    [Fact]
    public void GenerateSlotToken_ShouldProduceUniqueCjAdTokens()
    {
        var first = ArticleMarkdownRenderer.GenerateSlotToken();
        var second = ArticleMarkdownRenderer.GenerateSlotToken();

        Assert.StartsWith("{{CJ_AD_", first);
        Assert.EndsWith("}}", first);
        Assert.NotEqual(first, second);
    }
}