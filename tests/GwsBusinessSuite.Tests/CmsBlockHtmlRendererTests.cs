using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class CmsBlockHtmlRendererTests
{
    [Fact]
    public void Render_ShouldRenderHeroBlockWithTitleAndCta()
    {
        var html = CmsBlockHtmlRenderer.Render(
            "[{\"type\":\"hero\",\"title\":\"Welcome\",\"subtitle\":\"Intro\",\"primaryCta\":\"Start\",\"primaryCtaHref\":\"/start\"}]");

        Assert.Contains("Welcome", html);
        Assert.Contains("Intro", html);
        Assert.Contains("href=\"/start\"", html);
        Assert.Contains("Start", html);
    }

    [Fact]
    public void Render_ShouldHtmlEncodeUserSuppliedFields_ToPreventScriptInjection()
    {
        var html = CmsBlockHtmlRenderer.Render(
            "[{\"type\":\"hero\",\"title\":\"<script>alert(1)</script>\",\"subtitle\":\"\"}]");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Render_ShouldReturnEmptyString_ForEmptyBlocksArray()
    {
        var html = CmsBlockHtmlRenderer.Render("[]");

        Assert.Equal(string.Empty, html);
    }

    [Fact]
    public void Render_ShouldSkipUnknownBlockTypes_WithoutThrowing()
    {
        var html = CmsBlockHtmlRenderer.Render("[{\"type\":\"totally-unknown\",\"title\":\"x\"}]");

        Assert.DoesNotContain("totally-unknown", html);
    }

    [Fact]
    public void Render_ShouldRenderImageBlock_WithEncodedSrcAndAlt()
    {
        var html = CmsBlockHtmlRenderer.Render(
            "[{\"type\":\"image\",\"src\":\"/media/abc\",\"alt\":\"A photo\"}]");

        Assert.Contains("src=\"/media/abc\"", html);
        Assert.Contains("alt=\"A photo\"", html);
    }

    [Fact]
    public void Render_ShouldOmitImageBlock_WhenSrcIsMissing()
    {
        var html = CmsBlockHtmlRenderer.Render("[{\"type\":\"image\",\"alt\":\"A photo\"}]");

        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Render_ShouldRenderFeatureStackItemsAsListItems()
    {
        var html = CmsBlockHtmlRenderer.Render(
            "[{\"type\":\"feature-stack\",\"title\":\"What you get\",\"items\":[\"Automation\",\"Analytics\"]}]");

        Assert.Contains("<li>Automation</li>", html);
        Assert.Contains("<li>Analytics</li>", html);
    }

    [Fact]
    public void Render_ShouldRenderCountdownBlock_WithItsDaysValue()
    {
        var html = CmsBlockHtmlRenderer.Render(
            "[{\"type\":\"countdown\",\"title\":\"Launch countdown\",\"days\":12}]");

        Assert.Contains("Launch countdown", html);
        Assert.Contains(">12<", html);
        Assert.Contains("days remaining", html);
    }

    [Fact]
    public void Render_ShouldDefaultCountdownDaysToSeven_WhenMissing()
    {
        var html = CmsBlockHtmlRenderer.Render("[{\"type\":\"countdown\",\"title\":\"Coming soon\"}]");

        Assert.Contains(">7<", html);
    }

    [Fact]
    public void Render_ShouldRenderContactFormBlock_AsDisabledStaticFields()
    {
        var html = CmsBlockHtmlRenderer.Render("[{\"type\":\"contact-form\",\"title\":\"Get your plan\"}]");

        Assert.Contains("Get your plan", html);
        Assert.Contains("placeholder=\"Name\"", html);
        Assert.Contains("placeholder=\"Email\"", html);
        Assert.Contains("disabled", html);
    }

    [Fact]
    public void Render_ShouldRenderOnlyTheHeading_ForTocFaqAndTestimonials()
    {
        // These three have no real per-block content fields anywhere in the system (no
        // "items" array in any workflow blueprint) — the admin preview's body text for
        // them is editorial scaffolding, not real data, so it's deliberately not ported
        // to the public renderer. Heading-only is the correct, honest output.
        var toc = CmsBlockHtmlRenderer.Render("[{\"type\":\"toc\",\"title\":\"In this article\"}]");
        var faq = CmsBlockHtmlRenderer.Render("[{\"type\":\"faq\",\"title\":\"FAQ\"}]");
        var testimonials = CmsBlockHtmlRenderer.Render("[{\"type\":\"testimonials\",\"title\":\"Results\"}]");

        Assert.Contains("In this article", toc);
        Assert.DoesNotContain("Introduction", toc);

        Assert.Contains("FAQ", faq);
        Assert.DoesNotContain("Question and answer content.", faq);

        Assert.Contains("Results", testimonials);
        Assert.DoesNotContain("social proof", testimonials);
    }
}
