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
        // A real ArticleAffiliatePlacement (with a real Id) routes through the
        // /go/{placementId} click-tracking redirect rather than linking straight to the
        // CJ tracking URL - see BuildCardMarkup.
        Assert.Contains($"href=\"/go/{placement.Id:D}\"", rendered);
        Assert.DoesNotContain("https://example.com/track", rendered);
        Assert.Contains("Check it out", rendered);
        Assert.DoesNotContain("{{CJ_AD_abc12345}}", rendered);
    }

    [Fact]
    public void Render_ShouldStripOrphanTokens_WhenNoMatchingPlacementExists()
    {
        var rendered = ArticleMarkdownRenderer.Render(
            "Before.\n\n{{CJ_AD_deadbeef}}\n\nAfter.",
            Array.Empty<ArticleAffiliatePlacement>());

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
        // Even with a blank TrackingUrl, a real placement still routes through the
        // click-tracking redirect - the redirect endpoint itself is what falls back to "#"
        // for a placement with nothing to redirect to, not the render step.
        Assert.Contains($"href=\"/go/{placement.Id:D}\"", rendered);
    }

    [Fact]
    public void Render_ShouldHtmlEncodePlacementFields_ToPreventScriptInjection()
    {
        var placement = new ArticleAffiliatePlacement
        {
            SlotToken = "{{CJ_AD_xss}}",
            AdvertiserName = "<script>alert(1)</script>",
            Category = "\"><img src=x onerror=alert(1)>",
            TrackingUrl = "javascript:alert(1)\"onmouseover=\"alert(1)",
            CallToActionText = "Click <b>here</b>"
        };

        var rendered = ArticleMarkdownRenderer.Render("{{CJ_AD_xss}}", [placement]);

        Assert.DoesNotContain("<script>", rendered);
        Assert.DoesNotContain("\"><img", rendered);
        Assert.DoesNotContain("<b>here</b>", rendered);
        Assert.Contains("&lt;script&gt;", rendered);
    }

    [Fact]
    public void Render_AffiliatePlacementMarkupOverload_ShouldEncodeAndRenderTheSameAsEntityOverload()
    {
        var markup = new ArticleMarkdownRenderer.AffiliatePlacementMarkup(
            "{{CJ_AD_view}}", "Acme Tools", "Developer Tools", "https://example.com/track", "Check it out");

        var rendered = ArticleMarkdownRenderer.Render("{{CJ_AD_view}}", [markup]);

        Assert.Contains("Acme Tools", rendered);
        Assert.Contains("https://example.com/track", rendered);
        Assert.Contains("Check it out", rendered);
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