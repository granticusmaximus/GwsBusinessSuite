using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class CmsBlockHtmlRendererTests
{
    // Wraps a single widget in the minimal one-section/one-column layout envelope
    // (Section -> Column -> Widget) that CmsBlockHtmlRenderer.Render expects.
    private static string Layout(string widgetJson) =>
        $$"""{"sections":[{"id":"s1","columns":[{"id":"c1","widgets":[{{widgetJson}}]}]}]}""";

    [Fact]
    public void Render_ShouldRenderHeroWidget_WithHeadlineSublineAndCtas()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"hero","props":{"headline":"Welcome","subline":"Intro","cta1Label":"Start","cta1Href":"/start"}}"""));

        Assert.Contains("Welcome", html);
        Assert.Contains("Intro", html);
        Assert.Contains("href=\"/start\"", html);
        Assert.Contains("Start", html);
    }

    [Fact]
    public void Render_ShouldHtmlEncodeUserSuppliedFields_ToPreventScriptInjection()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"hero","props":{"headline":"<script>alert(1)</script>"}}"""));

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Render_ShouldReturnEmptyString_ForNoSections()
    {
        var html = CmsBlockHtmlRenderer.Render("""{"sections":[]}""");

        Assert.Equal(string.Empty, html);
    }

    [Fact]
    public void Render_ShouldReturnEmptyString_ForInvalidJson()
    {
        var html = CmsBlockHtmlRenderer.Render("not json");

        Assert.Equal(string.Empty, html);
    }

    [Fact]
    public void Render_ShouldSkipUnknownWidgetTypes_WithoutThrowing()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"totally-unknown","props":{"text":"x"}}"""));

        Assert.DoesNotContain("totally-unknown", html);
    }

    [Fact]
    public void Render_ShouldRenderImageWidget_WithEncodedSrcAndAlt()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"image","props":{"src":"/media/abc","alt":"A photo"}}"""));

        Assert.Contains("src=\"/media/abc\"", html);
        Assert.Contains("alt=\"A photo\"", html);
    }

    [Fact]
    public void Render_ShouldOmitImageWidget_WhenSrcIsMissing()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"image","props":{"alt":"A photo"}}"""));

        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Render_ShouldRenderHeadingWidget_WithRequestedLevel()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"heading","props":{"text":"Section title","level":"h3"}}"""));

        Assert.Contains("<h3", html);
        Assert.Contains("Section title", html);
        Assert.Contains("</h3>", html);
    }

    [Fact]
    public void Render_ShouldFallBackToH2_ForAnUnrecognizedHeadingLevel()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"heading","props":{"text":"x","level":"h9"}}"""));

        Assert.Contains("<h2", html);
    }

    [Fact]
    public void Render_ShouldRenderSpacerWidget_WithItsHeightValue()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"spacer","props":{"height":"120"}}"""));

        Assert.Contains("height:120px", html);
    }

    [Fact]
    public void Render_ShouldDefaultSpacerHeightTo48_WhenMissing()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"spacer","props":{}}"""));

        Assert.Contains("height:48px", html);
    }

    [Fact]
    public void Render_ShouldRenderFormWidget_WithCustomFieldsPostingToTheSubmitEndpoint()
    {
        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"form","props":{"submitLabel":"Send","fieldsJson":"[{\"key\":\"name\",\"label\":\"Name\",\"type\":\"text\",\"required\":true},{\"key\":\"favoriteColor\",\"label\":\"Favorite color\",\"type\":\"select\",\"optionsJson\":\"[\\\"Red\\\",\\\"Blue\\\"]\"}]"}}"""),
            "my-site",
            "contact");

        Assert.Contains("<form", html);
        Assert.Contains("action=\"/cms/my-site/submit\"", html);
        Assert.Contains("name=\"_path\" value=\"contact\"", html);
        Assert.Contains("name=\"name\"", html);
        Assert.Contains("required", html);
        Assert.Contains("name=\"favoriteColor\"", html);
        Assert.Contains("<option value=\"Red\">Red</option>", html);
        Assert.Contains("gws-form-honeypot", html);
        Assert.Contains("Send", html);
    }

    [Fact]
    public void Render_ShouldRenderFormWidget_WithNoFields_WithoutThrowing()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"form","props":{}}"""));

        Assert.Contains("<form", html);
        Assert.Contains("gws-form-honeypot", html);
    }

    [Fact]
    public void Render_ShouldApplySectionBackgroundAndPaddingClasses()
    {
        var html = CmsBlockHtmlRenderer.Render(
            """{"sections":[{"id":"s1","background":"dark","padding":"lg","columns":[]}]}""");

        Assert.Contains("gws-bg-dark", html);
        Assert.Contains("gws-pad-lg", html);
    }

    [Fact]
    public void Render_ShouldRenderMultipleColumns_UsingTheColumnLayoutClass()
    {
        var html = CmsBlockHtmlRenderer.Render(
            """{"sections":[{"id":"s1","columnLayout":"half-half","columns":[{"id":"c1","widgets":[]},{"id":"c2","widgets":[]}]}]}""");

        Assert.Contains("gws-cols-2", html);
        var columnCount = html.Split("class=\"gws-column\"").Length - 1;
        Assert.Equal(2, columnCount);
    }
}
