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
    public void Render_ShouldReturnCanvasPlaceholder_ForNoSections_InEditMode()
    {
        var html = CmsBlockHtmlRenderer.Render("""{"sections":[]}""", editMode: true);

        Assert.Contains("gws-canvas-empty", html);
        Assert.Contains("data-gws-empty-canvas", html);
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

    [Fact]
    public void Render_ShouldEmitCanvasDropMetadata_InEditMode()
    {
        var html = CmsBlockHtmlRenderer.Render(
            """{"sections":[{"id":"s1","columns":[{"id":"c1","widgets":[{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"}}]}]}]}""",
            editMode: true);

        Assert.Contains("data-gws-section-id=\"s1\"", html);
        Assert.Contains("data-gws-column-id=\"c1\"", html);
        Assert.Contains("data-gws-widget-id=\"w1\"", html);
    }

    [Fact]
    public void Render_ShouldShowEmptyColumnDropHint_InEditMode()
    {
        var html = CmsBlockHtmlRenderer.Render(
            """{"sections":[{"id":"s1","columns":[{"id":"c1","widgets":[]}]}]}""",
            editMode: true);

        Assert.Contains("gws-column-empty", html);
        Assert.Contains("Drop widgets here", html);
    }

    [Fact]
    public void Render_ShouldNotWrapWidget_WhenStyleHasNoOverrides()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"}}"""));

        Assert.DoesNotContain("gws-widget-style", html);
    }

    [Fact]
    public void Render_ShouldWrapWidgetInStyledDiv_WhenStyleOverridesAreSet()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"style":{"textColor":"#2563eb","backgroundColor":"#f1f5f9","padding":"md","borderRadius":"lg","fontSize":"xl"}}"""));

        Assert.Contains("gws-widget-style", html);
        Assert.Contains("color:#2563eb", html);
        Assert.Contains("background-color:#f1f5f9", html);
        Assert.Contains("padding:1.5rem", html);
        Assert.Contains("border-radius:20px", html);
        Assert.Contains("font-size:1.75rem", html);
    }

    [Fact]
    public void WidgetStyle_ToInlineStyle_ShouldReturnEmptyString_WhenAllFieldsAreDefault()
    {
        var style = new WidgetStyle();

        Assert.Equal(string.Empty, style.ToInlineStyle());
        Assert.False(style.HasAnyOverride);
    }

    [Fact]
    public void Render_ShouldRenderRichTextWidget_AsHtmlFromMarkdown()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"richtext","props":{"content":"Some **bold** and a [link](https://example.com)."}}"""));

        Assert.Contains("gws-richtext", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<a href=\"https://example.com\">link</a>", html);
    }

    [Fact]
    public void Render_ShouldRenderTestimonialWidget_WithQuoteAndAuthor()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"testimonial","props":{"quote":"Great product","authorName":"Jane Doe","authorRole":"CEO"}}"""));

        Assert.Contains("Great product", html);
        Assert.Contains("Jane Doe", html);
        Assert.Contains("CEO", html);
        Assert.Contains("gws-testimonial", html);
    }

    [Fact]
    public void Render_ShouldRenderAccordionWidget_WithCollapsibleItems()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"accordion","props":{"itemsJson":"[{\"question\":\"Q1?\",\"answer\":\"A1.\"}]"}}"""));

        Assert.Contains("<details", html);
        Assert.Contains("Q1?", html);
        Assert.Contains("A1.", html);
    }

    [Fact]
    public void Render_ShouldRenderNoDetailsElements_ForAccordionWidget_WithNoItems()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"accordion","props":{"itemsJson":"[]"}}"""));

        Assert.DoesNotContain("<details", html);
    }

    [Fact]
    public void Render_ShouldRenderPostsGridWidget_WithSuppliedArticles()
    {
        var articles = new List<PublicArticleSummary>
        {
            new("first-post", "First Post", "First summary", "/media/first.jpg", DateTimeOffset.UtcNow),
            new("second-post", "Second Post", "Second summary", null, DateTimeOffset.UtcNow.AddDays(-1))
        };

        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"posts-grid","props":{"count":"2","columns":"2"}}"""),
            articles: articles);

        Assert.Contains("First Post", html);
        Assert.Contains("href=\"/blog/first-post\"", html);
        Assert.Contains("First summary", html);
        Assert.Contains("Second Post", html);
        Assert.Contains("gws-posts-grid-cols-2", html);
    }

    [Fact]
    public void Render_ShouldRespectCountLimit_ForPostsGridWidget()
    {
        var articles = Enumerable.Range(1, 5)
            .Select(i => new PublicArticleSummary($"post-{i}", $"Post {i}", "", null, DateTimeOffset.UtcNow))
            .ToList();

        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"posts-grid","props":{"count":"2"}}"""),
            articles: articles);

        Assert.Contains("Post 1", html);
        Assert.Contains("Post 2", html);
        Assert.DoesNotContain("Post 3", html);
    }

    [Fact]
    public void Render_ShouldShowEmptyMessage_ForPostsGridWidget_WithNoArticles()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"posts-grid","props":{}}"""));

        Assert.Contains("No published posts yet.", html);
    }

    [Fact]
    public void Render_ShouldHideExcerptAndImage_ForPostsGridWidget_WhenToggledOff()
    {
        var articles = new List<PublicArticleSummary>
        {
            new("first-post", "First Post", "Should not appear", "/media/first.jpg", DateTimeOffset.UtcNow)
        };

        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"posts-grid","props":{"showImage":"false","showExcerpt":"false"}}"""),
            articles: articles);

        Assert.DoesNotContain("Should not appear", html);
        Assert.DoesNotContain("<img", html);
    }
}
