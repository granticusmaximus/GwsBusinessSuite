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
    public void Render_ShouldRenderWysiwygMarkdownAndEscapeRawHtml()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Use **strong guidance** and <script>alert(1)</script>."}}"""));

        Assert.Contains("<strong>strong guidance</strong>", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
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
    public void EditModeScript_ShouldReportCrossFrameDragTargetsAndCommittedDrops()
    {
        var script = CmsBlockHtmlRenderer.BuildEditModeScript();

        Assert.Contains("cms:external-drag-target", script);
        Assert.Contains("cms:external-drag-committed", script);
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
    public void Render_ShouldNotWrapWidget_WhenNoInteractionIsSet()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"}}"""));

        Assert.DoesNotContain("gws-interaction", html);
    }

    [Fact]
    public void Render_ShouldWrapWidgetWithInteractionData_WhenAnInteractionIsSet()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"interaction":{"trigger":"scrollIntoView","action":"slideInUp","durationMs":450,"delayMs":100,"once":false}}"""));

        Assert.Contains("gws-interaction", html);
        Assert.Contains("data-gws-interaction=", html);
        Assert.Contains("scrollIntoView", html);
        Assert.Contains("slideInUp", html);
        Assert.Contains("450", html);
        Assert.Contains("false", html);
    }

    [Fact]
    public void Render_ShouldNotWrapWidgetWithInteractionData_InEditMode()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"interaction":{"trigger":"pageLoad","action":"fadeIn"}}"""),
            editMode: true);

        Assert.DoesNotContain("gws-interaction", html);
    }

    [Fact]
    public void Render_ShouldIgnoreAnInteraction_WithAnUnrecognizedTriggerOrAction()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"interaction":{"trigger":"onKeyPress","action":"fadeIn"}}"""));

        Assert.DoesNotContain("gws-interaction", html);
    }

    [Fact]
    public void Render_ShouldClampInteractionDurationAndDelay_ToATenSecondCeiling()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"interaction":{"trigger":"pageLoad","action":"fadeIn","durationMs":999999,"delayMs":-50}}"""));

        Assert.Contains("&quot;durationMs&quot;:10000", html);
        Assert.Contains("&quot;delayMs&quot;:0", html);
    }

    [Fact]
    public void InteractionRuntimeScript_ShouldHandleAllFourTriggerKinds()
    {
        var script = CmsBlockHtmlRenderer.BuildInteractionRuntimeScript();

        Assert.Contains("data-gws-interaction", script);
        Assert.Contains("pageLoad", script);
        Assert.Contains("scrollIntoView", script);
        Assert.Contains("IntersectionObserver", script);
        Assert.Contains("prefers-reduced-motion", script);
    }

    [Fact]
    public void Render_ShouldShowALockedBadge_InEditModeOnly_ForAnExplicitlyLockedWidget()
    {
        var blocksJson = Layout("""{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"editPermission":"Locked"}""");

        var editModeHtml = CmsBlockHtmlRenderer.Render(blocksJson, editMode: true);
        var publicHtml = CmsBlockHtmlRenderer.Render(blocksJson, editMode: false);

        Assert.Contains("Locked", editModeHtml);
        Assert.DoesNotContain("Locked", publicHtml);
    }

    [Fact]
    public void Render_ShouldCombineVisibilityAndLockBadges_IntoOneHint_NotTwoOverlappingOnes()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"visibility":{"mode":"LoggedInOnly"},"editPermission":"ContentOnly"}"""),
            editMode: true);

        Assert.Contains("Logged-in only | Content only", html);
        var hintCount = System.Text.RegularExpressions.Regex.Matches(html, "gws-visibility-hint").Count;
        Assert.Equal(1, hintCount);
    }

    [Fact]
    public void Render_ShouldNotShowABadge_ForAnInheritedOrOpenEditPermission()
    {
        var inheritHtml = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"}}"""), editMode: true);
        var openHtml = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"editPermission":"Open"}"""), editMode: true);

        Assert.DoesNotContain("gws-visibility-hint", inheritHtml);
        Assert.DoesNotContain("gws-visibility-hint", openHtml);
    }

    [Fact]
    public void Render_ShouldResolveAColorToken_OverRawTextColor_WhenTokensAreSupplied()
    {
        var tokens = new DesignTokenSet([new DesignToken("Primary", "#1c3d5a")], [], []);

        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"style":{"textColor":"#000000","textColorToken":"Primary"}}"""),
            tokens: tokens);

        Assert.Contains("color:#1c3d5a", html);
        Assert.DoesNotContain("color:#000000", html);
    }

    [Fact]
    public void Render_ShouldFallBackToRawTextColor_WhenTheReferencedTokenDoesNotExist()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"style":{"textColor":"#000000","textColorToken":"NoSuchToken"}}"""),
            tokens: DesignTokenSet.Empty);

        Assert.Contains("color:#000000", html);
    }

    [Fact]
    public void WidgetStyle_ToInlineStyle_ShouldReturnEmptyString_WhenAllFieldsAreDefault()
    {
        var style = new WidgetStyle();

        Assert.Equal(string.Empty, style.ToInlineStyle());
        Assert.False(style.HasAnyOverride);
    }

    [Fact]
    public void WidgetStyle_ToInlineStyle_ShouldResolveBackgroundAndFontSizeTokens()
    {
        var tokens = new DesignTokenSet(
            [new DesignToken("Surface", "#f7f5f1")],
            [new TypeScaleStep("Lead", "1.25rem")],
            []);
        var style = new WidgetStyle { BackgroundColorToken = "Surface", FontSizeToken = "Lead" };

        Assert.True(style.HasAnyOverride);
        var inline = style.ToInlineStyle(tokens);
        Assert.Contains("background-color:#f7f5f1", inline);
        Assert.Contains("font-size:1.25rem", inline);
    }

    [Fact]
    public void WidgetStyle_ToInlineStyle_ShouldBehaveIdentically_WhenNoTokensArgumentIsPassed()
    {
        var style = new WidgetStyle { TextColor = "#111111" };

        Assert.Equal(style.ToInlineStyle(), style.ToInlineStyle(null));
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

    [Fact]
    public void LayoutContainsPostsGrid_ShouldReturnTrue_WhenAWidgetIsPostsGrid()
    {
        var layout = CmsBuilderJson.ParseLayout(Layout("""{"id":"w1","widgetType":"posts-grid","props":{}}"""));

        Assert.True(CmsBlockHtmlRenderer.LayoutContainsPostsGrid(layout));
    }

    [Fact]
    public void LayoutContainsPostsGrid_ShouldReturnFalse_WhenNoWidgetIsPostsGrid()
    {
        var layout = CmsBuilderJson.ParseLayout(Layout("""{"id":"w1","widgetType":"heading","props":{"text":"Hi"}}"""));

        Assert.False(CmsBlockHtmlRenderer.LayoutContainsPostsGrid(layout));
    }

    [Fact]
    public void LayoutContainsPostsGrid_ShouldReturnFalse_ForNullLayout()
    {
        Assert.False(CmsBlockHtmlRenderer.LayoutContainsPostsGrid(null));
    }

    [Fact]
    public void Render_ShouldOmitLoggedInOnlyWidget_ForAnonymousVisitor()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"members-only"},"visibility":{"mode":"LoggedInOnly"}}"""),
            isLoggedIn: false);

        Assert.DoesNotContain("members-only", html);
    }

    [Fact]
    public void Render_ShouldRenderLoggedInOnlyWidget_ForLoggedInVisitor()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"members-only"},"visibility":{"mode":"LoggedInOnly"}}"""),
            isLoggedIn: true);

        Assert.Contains("members-only", html);
    }

    [Fact]
    public void Render_ShouldOmitHomepageOnlyWidget_OnANonHomepagePage()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"welcome-banner"},"visibility":{"mode":"HomepageOnly"}}"""),
            pageSlug: "about");

        Assert.DoesNotContain("welcome-banner", html);
    }

    [Fact]
    public void Render_ShouldRenderHomepageOnlyWidget_OnTheHomePage()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"welcome-banner"},"visibility":{"mode":"HomepageOnly"}}"""),
            pageSlug: "home");

        Assert.Contains("welcome-banner", html);
    }

    [Theory]
    [InlineData("blog/my-first-post", true)]
    [InlineData("blog/nested/post", true)]
    [InlineData("about", false)]
    public void Render_ShouldEvaluateUrlPatternWildcardAgainstThePageSlug(string pageSlug, bool shouldRender)
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"blog-only-widget"},"visibility":{"mode":"UrlPattern","urlPattern":"blog/*"}}"""),
            pageSlug: pageSlug);

        Assert.Equal(shouldRender, html.Contains("blog-only-widget"));
    }

    [Fact]
    public void Render_ShouldAlwaysRenderConditionalWidgets_InEditMode_AndShowAVisibilityBadge()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"members-only"},"visibility":{"mode":"LoggedInOnly"}}"""),
            pageSlug: "about", editMode: true, isLoggedIn: false);

        Assert.Contains("members-only", html);
        Assert.Contains("gws-visibility-hint", html);
        Assert.Contains("Logged-in only", html);
    }

    [Fact]
    public void ShouldRenderWidget_ShouldReturnTrue_ForTheDefaultAlwaysMode()
    {
        Assert.True(CmsBlockHtmlRenderer.ShouldRenderWidget(new VisibilityRule(), "anything", isLoggedIn: false));
    }

    [Fact]
    public void PlainTextPreview_ShouldReturnAShortSingleLineSummary_PerWidgetType()
    {
        var widget = new LayoutWidget { WidgetType = "heading", Props = new() { ["text"] = "Line one\nLine two" } };

        Assert.Equal("Line one Line two", CmsBlockHtmlRenderer.PlainTextPreview(widget));
    }

    [Fact]
    public void PlainTextPreview_ShouldTruncateLongText()
    {
        var widget = new LayoutWidget { WidgetType = "paragraph", Props = new() { ["text"] = new string('x', 200) } };

        var preview = CmsBlockHtmlRenderer.PlainTextPreview(widget, maxLength: 10);

        Assert.Equal(11, preview.Length); // 10 chars + ellipsis
        Assert.EndsWith("…", preview);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    public void Render_ShouldRejectDangerousUriSchemes_InButtonHref(string dangerousHref)
    {
        // Built via the typed model + the real serializer (not hand-spliced JSON text) so an
        // exotic value like a raw tab character is escaped exactly the way a genuinely stored
        // BlocksJson value would be, rather than producing invalid JSON in the test itself.
        var layout = ButtonLayout(dangerousHref);
        var html = CmsBlockHtmlRenderer.Render(CmsBuilderJson.Serialize(layout));

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"#\"", html);
    }

    [Fact]
    public void Render_ShouldRejectDangerousUriSchemes_InHeroCtaHref()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"hero","props":{"headline":"Hi","cta1Label":"Go","cta1Href":"javascript:alert(document.cookie)"}}"""));

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"#\"", html);
    }

    [Fact]
    public void Render_ShouldRejectDangerousUriSchemes_InCardLink()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"card","props":{"title":"T","link":"javascript:alert(1)"}}"""));

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"#\"", html);
    }

    [Theory]
    [InlineData("/about")]
    [InlineData("about")]
    [InlineData("#section")]
    [InlineData("?query=1")]
    [InlineData("https://example.com/path")]
    [InlineData("http://example.com")]
    [InlineData("mailto:hello@example.com")]
    [InlineData("tel:+15551234567")]
    public void Render_ShouldPreserveLegitimateHrefValues(string safeHref)
    {
        var html = CmsBlockHtmlRenderer.Render(CmsBuilderJson.Serialize(ButtonLayout(safeHref)));

        Assert.Contains($"href=\"{safeHref}\"", html);
    }

    [Fact]
    public void Render_ShouldAbsolutelyPositionWidgets_InAFreeformSection()
    {
        var layout = new PageLayout
        {
            Sections =
            [
                new LayoutSection
                {
                    LayoutMode = CmsSectionLayoutModes.Freeform,
                    FreeformHeightPx = 600,
                    Columns =
                    [
                        new LayoutColumn
                        {
                            Widgets =
                            [
                                new LayoutWidget
                                {
                                    WidgetType = "heading",
                                    Props = new() { ["text"] = "Freeform heading" },
                                    Freeform = new FreeformPosition { X = 10, Y = 15, Width = 40, Height = 25, Z = 2 }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var html = CmsBlockHtmlRenderer.Render(CmsBuilderJson.Serialize(layout));

        Assert.Contains("gws-section-freeform-canvas", html);
        Assert.Contains("height:600px", html);
        Assert.Contains("gws-freeform-item", html);
        Assert.Contains("left:10%;top:15%;width:40%;height:25%;z-index:2;", html);
        Assert.Contains("Freeform heading", html);
        Assert.DoesNotContain("gws-columns", html);
    }

    [Fact]
    public void Render_ShouldFallBackToADefaultScatteredPosition_ForAFreeformWidgetWithNoExplicitBox()
    {
        var layout = new PageLayout
        {
            Sections =
            [
                new LayoutSection
                {
                    LayoutMode = CmsSectionLayoutModes.Freeform,
                    Columns = [new LayoutColumn { Widgets = [new LayoutWidget { WidgetType = "heading", Props = new() { ["text"] = "No box yet" } }] }]
                }
            ]
        };

        var html = CmsBlockHtmlRenderer.Render(CmsBuilderJson.Serialize(layout));

        Assert.Contains("gws-freeform-item", html);
        Assert.Contains("No box yet", html);
    }

    [Fact]
    public void Render_ShouldStillUseColumnGrid_ForAFlowSection_WithDefaultLayoutMode()
    {
        var layout = new PageLayout
        {
            Sections = [new LayoutSection { Columns = [new LayoutColumn { Widgets = [new LayoutWidget { WidgetType = "heading", Props = new() { ["text"] = "Flow heading" } }] }] }]
        };

        var html = CmsBlockHtmlRenderer.Render(CmsBuilderJson.Serialize(layout));

        Assert.Contains("gws-columns", html);
        Assert.DoesNotContain("gws-section-freeform-canvas", html);
        Assert.DoesNotContain("gws-freeform-item", html);
    }

    [Fact]
    public void Render_ShouldOmitAVisibilityHiddenWidget_InAFreeformSection_OutsideEditMode()
    {
        var layout = new PageLayout
        {
            Sections =
            [
                new LayoutSection
                {
                    LayoutMode = CmsSectionLayoutModes.Freeform,
                    Columns =
                    [
                        new LayoutColumn
                        {
                            Widgets =
                            [
                                new LayoutWidget
                                {
                                    WidgetType = "heading",
                                    Props = new() { ["text"] = "Members only" },
                                    Visibility = new VisibilityRule { Mode = VisibilityModes.LoggedInOnly },
                                    Freeform = new FreeformPosition()
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var html = CmsBlockHtmlRenderer.Render(CmsBuilderJson.Serialize(layout), editMode: false, isLoggedIn: false);

        Assert.DoesNotContain("Members only", html);
    }

    private static PageLayout ButtonLayout(string href) => new()
    {
        Sections =
        [
            new LayoutSection
            {
                Columns =
                [
                    new LayoutColumn
                    {
                        Widgets =
                        [
                            new LayoutWidget
                            {
                                WidgetType = "button",
                                Props = new() { ["label"] = "Click", ["href"] = href }
                            }
                        ]
                    }
                ]
            }
        ]
    };
}
