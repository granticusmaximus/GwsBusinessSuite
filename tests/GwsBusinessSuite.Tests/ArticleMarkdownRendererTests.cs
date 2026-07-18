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
            LinkName = "20% off Acme Pro",
            Category = "Developer Tools",
            TrackingUrl = "https://example.com/track",
            CallToActionText = "Check it out"
        };

        var rendered = ArticleMarkdownRenderer.Render(
            "Intro text.\n\n{{CJ_AD_abc12345}}\n\nMore text.",
            [placement]);

        // The visible card links with the offer's own name, not a synthesized
        // "advertiser + category" phrase - see BuildCardMarkup.
        Assert.Contains("Sponsored Pick:", rendered);
        Assert.Contains("20% off Acme Pro", rendered);
        Assert.DoesNotContain("Category:", rendered);
        // A real ArticleAffiliatePlacement (with a real Id) routes through the
        // /go/{placementId} click-tracking redirect rather than linking straight to the
        // CJ tracking URL - see BuildCardMarkup.
        Assert.Contains($"href=\"/go/{placement.Id:D}\"", rendered);
        Assert.DoesNotContain("https://example.com/track", rendered);
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
    public void Render_ShouldFallBackToCallToActionText_WhenLinkNameIsMissing()
    {
        var placement = new ArticleAffiliatePlacement
        {
            SlotToken = "{{CJ_AD_xyz}}",
            AdvertiserName = "Acme Tools",
            LinkName = string.Empty,
            Category = string.Empty,
            TrackingUrl = string.Empty,
            CallToActionText = "Explore Offer"
        };

        var rendered = ArticleMarkdownRenderer.Render("{{CJ_AD_xyz}}", [placement]);

        // Older placements saved before LinkName existed still render something clickable.
        Assert.Contains("Explore Offer", rendered);
        Assert.DoesNotContain("Category:", rendered);
        // Even with a blank TrackingUrl, a real placement still routes through the
        // click-tracking redirect - the redirect endpoint itself is what falls back to a
        // 404 for a placement with nothing to redirect to, not the render step.
        Assert.Contains($"href=\"/go/{placement.Id:D}\"", rendered);
    }

    [Fact]
    public void Render_ShouldIncludeImage_WhenImageUrlIsProvided()
    {
        var placement = new ArticleAffiliatePlacement
        {
            SlotToken = "{{CJ_AD_img}}",
            AdvertiserName = "Acme Tools",
            LinkName = "Shop the sale",
            TrackingUrl = "https://example.com/track",
            ImageUrl = "https://cdn.example.com/acme-banner.png"
        };

        var rendered = ArticleMarkdownRenderer.Render("{{CJ_AD_img}}", [placement]);

        Assert.Contains("<img src=\"https://cdn.example.com/acme-banner.png\"", rendered);
        Assert.Contains("Shop the sale", rendered);
    }

    [Fact]
    public void Render_ShouldOmitImage_WhenImageUrlIsMissing()
    {
        var placement = new ArticleAffiliatePlacement
        {
            SlotToken = "{{CJ_AD_noimg}}",
            AdvertiserName = "Acme Tools",
            LinkName = "Shop the sale",
            TrackingUrl = "https://example.com/track"
        };

        var rendered = ArticleMarkdownRenderer.Render("{{CJ_AD_noimg}}", [placement]);

        Assert.DoesNotContain("<img", rendered);
    }

    [Fact]
    public void Render_ShouldHtmlEncodePlacementFields_ToPreventScriptInjection()
    {
        var placement = new ArticleAffiliatePlacement
        {
            SlotToken = "{{CJ_AD_xss}}",
            AdvertiserName = "<script>alert(1)</script>",
            LinkName = "Click <b>here</b>",
            TrackingUrl = "javascript:alert(1)\"onmouseover=\"alert(1)",
            ImageUrl = "x\" onerror=\"alert(1)"
        };

        var rendered = ArticleMarkdownRenderer.Render("{{CJ_AD_xss}}", [placement]);

        Assert.DoesNotContain("<script>", rendered);
        Assert.DoesNotContain("<b>here</b>", rendered);
        // The malicious ImageUrl's quote must not break out of the src attribute into a new
        // onerror attribute - it should show up only as the HTML-encoded attribute value.
        Assert.DoesNotContain("onerror=\"alert(1)\"", rendered);
        Assert.Contains("&lt;script&gt;", rendered);
        Assert.Contains("&lt;b&gt;here&lt;/b&gt;", rendered);
        Assert.Contains("src=\"x&quot; onerror=&quot;alert(1)\"", rendered);
    }

    [Fact]
    public void Render_AffiliatePlacementMarkupOverload_ShouldEncodeAndRenderTheSameAsEntityOverload()
    {
        var markup = new ArticleMarkdownRenderer.AffiliatePlacementMarkup(
            SlotToken: "{{CJ_AD_view}}",
            AdvertiserName: "Acme Tools",
            Category: "Developer Tools",
            TrackingUrl: "https://example.com/track",
            CallToActionText: "Check it out",
            LinkName: "20% off Acme Pro");

        var rendered = ArticleMarkdownRenderer.Render("{{CJ_AD_view}}", [markup]);

        Assert.Contains("20% off Acme Pro", rendered);
        Assert.Contains("https://example.com/track", rendered);
    }

    [Fact]
    public void Render_ShouldAppendAutomaticRotationWithoutChangingManualTokens()
    {
        var rotationId = Guid.NewGuid();
        var rotation = new ArticleMarkdownRenderer.AffiliatePlacementMarkup(
            SlotToken: string.Empty,
            AdvertiserName: "Rotating Partner",
            Category: "Software",
            TrackingUrl: "https://example.com/rotating",
            CallToActionText: "View offer",
            PlacementId: rotationId,
            LinkName: "Rotating Partner deal");

        var rendered = ArticleMarkdownRenderer.Render(
            "Original article body.",
            Array.Empty<ArticleAffiliatePlacement>(),
            rotation);

        Assert.StartsWith("Original article body.", rendered);
        Assert.Contains("Rotating Partner deal", rendered);
        Assert.Contains($"href=\"/go/{rotationId:D}\"", rendered);
        Assert.DoesNotContain("https://example.com/rotating", rendered);
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
