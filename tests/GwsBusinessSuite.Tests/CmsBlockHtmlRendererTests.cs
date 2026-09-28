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
    public void Render_ShouldRenderHeroVideoBackground_WhenLayoutIsVideoBackgroundAndUrlIsSet()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"hero","props":{"headline":"Welcome","layout":"video-background","backgroundVideoUrl":"/media/hero.mp4","posterImageUrl":"/media/poster.jpg","overlayOpacity":"60"}}"""));

        Assert.Contains("gws-hero-video", html);
        Assert.Contains("src=\"/media/hero.mp4\"", html);
        Assert.Contains("poster=\"/media/poster.jpg\"", html);
        Assert.Contains("opacity:60%", html);
        Assert.Contains("autoplay", html);
        Assert.Contains("Welcome", html);
    }

    [Fact]
    public void Render_ShouldFallBackToDefaultHero_WhenVideoBackgroundLayoutHasNoVideoUrl()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"hero","props":{"headline":"Welcome","layout":"video-background"}}"""));

        Assert.DoesNotContain("gws-hero-video", html);
        Assert.DoesNotContain("<video", html);
    }

    [Fact]
    public void Render_ShouldClampHeroOverlayOpacity_ToZeroToOneHundred()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"hero","props":{"headline":"Welcome","layout":"video-background","backgroundVideoUrl":"/media/hero.mp4","overlayOpacity":"999"}}"""));

        Assert.Contains("opacity:100%", html);
    }

    [Fact]
    public void Render_ShouldRenderHeroSplitLayout_WithImageAndTextInThePickedOrder()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"hero","props":{"headline":"Welcome","layout":"split","splitImageUrl":"/media/side.jpg","splitImagePosition":"left"}}"""));

        Assert.Contains("gws-hero-split gws-hero-split-left", html);
        Assert.Contains("src=\"/media/side.jpg\"", html);
        var imgIndex = html.IndexOf("gws-hero-split-img", StringComparison.Ordinal);
        var headlineIndex = html.IndexOf("Welcome", StringComparison.Ordinal);
        Assert.True(imgIndex < headlineIndex, "the image should come before the text when splitImagePosition is 'left'");
    }

    [Fact]
    public void Render_ShouldFallBackToDefaultHero_WhenSplitLayoutHasNoImageUrl()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"hero","props":{"headline":"Welcome","layout":"split"}}"""));

        Assert.DoesNotContain("gws-hero-split", html);
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
    public void Render_ShouldNotCollideTheHoneypotFieldName_WithARealFieldKeyedCompany()
    {
        // Regression test: the honeypot used to be name="company", which is exactly the HTML
        // name a real field labeled "Company" (the documented Company FormFieldRole) derives.
        // Two same-named inputs merge into one posted value, so a genuine visitor's own company
        // name made the honeypot check look tripped, silently discarding every real submission
        // through a form that used this field - see CmsBlockHtmlRenderer.cs's comment on the
        // honeypot input for the incident this test locks in place.
        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"form","props":{"fieldsJson":"[{\"key\":\"company\",\"label\":\"Company\",\"type\":\"text\",\"role\":\"company\"}]"}}"""));

        Assert.Contains("name=\"company\"", html);
        Assert.DoesNotContain("name=\"company\" class=\"gws-form-honeypot\"", html);
        Assert.Contains("gws-form-honeypot", html);
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
    public void Render_ShouldApplyColorSchemeInlineStyle_ToASection_WhenBackgroundTokenResolves()
    {
        var tokens = new DesignTokenSet([new DesignToken("Accent", "#1c3d5a")], [], []);

        var html = CmsBlockHtmlRenderer.Render(
            """{"sections":[{"id":"s1","backgroundColorToken":"Accent","textColor":"#f8fafc","columns":[]}]}""",
            tokens: tokens);

        Assert.Contains("background-color:#1c3d5a", html);
        Assert.Contains("color:#f8fafc", html);
    }

    [Fact]
    public void Render_ShouldNotAddInlineStyle_ToASection_WhenColorSchemeTokenDoesNotResolve()
    {
        var html = CmsBlockHtmlRenderer.Render(
            """{"sections":[{"id":"s1","backgroundColorToken":"Nonexistent","columns":[]}]}""");

        Assert.DoesNotContain("style=", html);
    }

    [Fact]
    public void Render_ShouldApplyHiddenOnMobileAndTabletClasses_ToASection()
    {
        var html = CmsBlockHtmlRenderer.Render(
            """{"sections":[{"id":"s1","hiddenOnMobile":true,"hiddenOnTablet":true,"columns":[]}]}""");

        Assert.Contains("gws-hide-mobile", html);
        Assert.Contains("gws-hide-tablet", html);
    }

    [Fact]
    public void Render_ShouldNotAddHiddenClasses_ToASection_WhenNeitherFlagIsSet()
    {
        var html = CmsBlockHtmlRenderer.Render(
            """{"sections":[{"id":"s1","columns":[]}]}""");

        Assert.DoesNotContain("gws-hide-mobile", html);
        Assert.DoesNotContain("gws-hide-tablet", html);
    }

    [Fact]
    public void Render_ShouldWrapWidgetWithHiddenClass_WhenHiddenOnMobileIsSet_EvenWithNoStyleOverride()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"hiddenOnMobile":true}"""));

        Assert.Contains("gws-widget-style", html);
        Assert.Contains("gws-hide-mobile", html);
        Assert.DoesNotContain("gws-hide-tablet", html);
    }

    [Fact]
    public void Render_ShouldNotWrapWidget_WhenNeitherStyleNorHiddenFlagsAreSet()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"paragraph","props":{"text":"Hello"},"hiddenOnMobile":false,"hiddenOnTablet":false}"""));

        Assert.DoesNotContain("gws-widget-style", html);
        Assert.DoesNotContain("gws-hide-mobile", html);
        Assert.DoesNotContain("gws-hide-tablet", html);
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
        // The behaviour moved out of an inline <script> and into /js/cms-edit-mode.js, because
        // the app's CSP forbids inline scripts and was silently blocking the whole canvas (see
        // CmsEditModeScriptCspTests). The drag protocol assertions still belong - they just have
        // to read the shipped file now.
        var scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot/js/cms-edit-mode.js"));
        Assert.True(File.Exists(scriptPath), $"Edit-mode script is missing: {scriptPath}");
        var script = File.ReadAllText(scriptPath);

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
    public void Render_ShouldRenderAuthorBoxWidget_WithNameRoleBioAndAvatar()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"author-box","props":{"name":"Jane Doe","roleOrTitle":"Senior Editor","bio":"Writes about **things**.","avatarUrl":"https://example.com/jane.jpg"}}"""));

        Assert.Contains("gws-author-box", html);
        Assert.Contains("Jane Doe", html);
        Assert.Contains("Senior Editor", html);
        Assert.Contains("<strong>things</strong>", html);
        Assert.Contains("src=\"https://example.com/jane.jpg\"", html);
    }

    [Theory]
    [InlineData("websiteUrl", "https://jane.example")]
    [InlineData("twitterUrl", "https://x.com/jane")]
    [InlineData("linkedinUrl", "https://linkedin.com/in/jane")]
    [InlineData("emailAddress", "jane@example.com")]
    public void Render_ShouldHideAuthorBoxSocialLink_WhenItsOwnPropIsEmpty(string emptyField, string valueWhenPresent)
    {
        var props = new Dictionary<string, string>
        {
            ["name"] = "Jane Doe",
            ["websiteUrl"] = "https://jane.example",
            ["twitterUrl"] = "https://x.com/jane",
            ["linkedinUrl"] = "https://linkedin.com/in/jane",
            ["emailAddress"] = "jane@example.com",
        };
        props[emptyField] = "";
        var widget = new LayoutWidget { WidgetType = "author-box", Props = props };

        var html = CmsBlockHtmlRenderer.Render(new PageLayout
        {
            Sections = [new LayoutSection { Columns = [new LayoutColumn { Widgets = [widget] }] }]
        });

        Assert.DoesNotContain(valueWhenPresent, html);
        var socialLinkCount = System.Text.RegularExpressions.Regex.Matches(html, "gws-author-box-social-link").Count;
        Assert.Equal(3, socialLinkCount);
    }

    [Fact]
    public void Render_ShouldShowAllFourAuthorBoxSocialLinks_WhenEveryFieldIsSet()
    {
        var widget = new LayoutWidget
        {
            WidgetType = "author-box",
            Props = new()
            {
                ["name"] = "Jane Doe",
                ["websiteUrl"] = "https://jane.example",
                ["twitterUrl"] = "https://x.com/jane",
                ["linkedinUrl"] = "https://linkedin.com/in/jane",
                ["emailAddress"] = "jane@example.com",
            }
        };

        var html = CmsBlockHtmlRenderer.Render(new PageLayout
        {
            Sections = [new LayoutSection { Columns = [new LayoutColumn { Widgets = [widget] }] }]
        });

        var socialLinkCount = System.Text.RegularExpressions.Regex.Matches(html, "gws-author-box-social-link").Count;
        Assert.Equal(4, socialLinkCount);
    }

    [Fact]
    public void PlainTextPreview_ShouldReturnTheAuthorName_ForAuthorBoxWidget()
    {
        var widget = new LayoutWidget { WidgetType = "author-box", Props = new() { ["name"] = "Jane Doe" } };

        Assert.Equal("Jane Doe", CmsBlockHtmlRenderer.PlainTextPreview(widget));
    }

    [Theory]
    [InlineData("info", "gws-callout-info")]
    [InlineData("success", "gws-callout-success")]
    [InlineData("warning", "gws-callout-warning")]
    [InlineData("danger", "gws-callout-danger")]
    [InlineData("note", "gws-callout-note")]
    public void Render_ShouldRenderCalloutWidget_WithTheVariantsOwnCssClass(string variant, string expectedClass)
    {
        var json = """{"id":"w1","widgetType":"callout","props":{"variant":"__VARIANT__","title":"Heads up","body":"Read this."}}"""
            .Replace("__VARIANT__", variant);
        var html = CmsBlockHtmlRenderer.Render(Layout(json));

        Assert.Contains(expectedClass, html);
        Assert.Contains("Heads up", html);
        Assert.Contains("Read this.", html);
    }

    [Fact]
    public void Render_ShouldFallBackToInfoVariant_ForAnUnrecognizedCalloutVariant()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"callout","props":{"variant":"not-a-real-variant","body":"Text"}}"""));

        Assert.Contains("gws-callout-info", html);
    }

    [Fact]
    public void Render_ShouldOmitTheCalloutTitleElement_WhenTitleIsNotSet()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"callout","props":{"body":"Text only."}}"""));

        Assert.DoesNotContain("gws-callout-title", html);
    }

    [Fact]
    public void Render_ShouldOmitTheCalloutIcon_WhenShowIconIsFalse()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"callout","props":{"body":"Text","showIcon":"false"}}"""));

        Assert.DoesNotContain("gws-callout-icon", html);
    }

    [Fact]
    public void PlainTextPreview_ShouldReturnTheTitle_ForCalloutWidget()
    {
        var widget = new LayoutWidget { WidgetType = "callout", Props = new() { ["title"] = "Heads up", ["body"] = "Details" } };

        Assert.Equal("Heads up", CmsBlockHtmlRenderer.PlainTextPreview(widget));
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
    public void Render_ShouldNotEmitFaqSchema_WhenIsFaqIsNotSet()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"accordion","props":{"itemsJson":"[{\"question\":\"Q1?\",\"answer\":\"A1.\"}]"}}"""));

        Assert.DoesNotContain("application/ld+json", html);
    }

    [Fact]
    public void Render_ShouldEmitFaqPageSchema_WhenIsFaqIsTrue()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"accordion","props":{"isFaq":"true","itemsJson":"[{\"question\":\"Q1?\",\"answer\":\"A1.\"},{\"question\":\"Q2?\",\"answer\":\"A2.\"}]"}}"""));

        Assert.Contains("""<script type="application/ld+json">""", html);
        Assert.Contains("\"@context\":\"https://schema.org\"", html);
        Assert.Contains("\"@type\":\"FAQPage\"", html);
        Assert.Contains("Q1?", html);
        Assert.Contains("Q2?", html);
        Assert.Contains("\"@type\":\"Question\"", html);
        Assert.Contains("\"@type\":\"Answer\"", html);
    }

    [Fact]
    public void Render_ShouldNotEmitFaqSchema_InEditMode_EvenWhenIsFaqIsTrue()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"accordion","props":{"isFaq":"true","itemsJson":"[{\"question\":\"Q1?\",\"answer\":\"A1.\"}]"}}"""),
            editMode: true);

        Assert.DoesNotContain("application/ld+json", html);
    }

    [Fact]
    public void Render_ShouldSkipEmptyAnswerQuestions_InFaqSchema()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"accordion","props":{"isFaq":"true","itemsJson":"[{\"question\":\"Q1?\",\"answer\":\"\"},{\"question\":\"Q2?\",\"answer\":\"A2.\"}]"}}"""));

        // Q1 still renders as a normal (empty-answer) accordion item - only excluded from the
        // FAQPage schema, since an unanswered question is not real structured FAQ data.
        var schemaCount = System.Text.RegularExpressions.Regex.Matches(html, "\"@type\":\"Question\"").Count;
        Assert.Equal(1, schemaCount);
        Assert.Contains("Q2?", html);
    }

    [Fact]
    public void Render_FaqSchema_ShouldEscapeHtmlSoScriptTagCannotBeBrokenOut()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"accordion","props":{"isFaq":"true","itemsJson":"[{\"question\":\"Q1</script><script>alert(1)</script>\",\"answer\":\"A1.\"}]"}}"""));

        Assert.DoesNotContain("</script><script>alert(1)</script>", html);
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
    public void Render_ShouldRenderListLayout_ForPostsGridWidget()
    {
        var articles = new List<PublicArticleSummary>
        {
            new("first-post", "First Post", "First summary", "/media/first.jpg", DateTimeOffset.UtcNow)
        };

        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"posts-grid","props":{"layout":"list"}}"""),
            articles: articles);

        Assert.Contains("gws-posts-grid-list", html);
        Assert.Contains("gws-posts-grid-list-item", html);
        Assert.Contains("gws-posts-grid-list-img", html);
        Assert.DoesNotContain("gws-posts-grid-cols", html);
    }

    [Fact]
    public void Render_ShouldRenderClassicLayout_ForPostsGridWidget()
    {
        var articles = new List<PublicArticleSummary>
        {
            new("first-post", "First Post", "First summary", "/media/first.jpg", DateTimeOffset.UtcNow)
        };

        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"posts-grid","props":{"layout":"classic"}}"""),
            articles: articles);

        Assert.Contains("gws-posts-grid-classic", html);
        Assert.Contains("gws-posts-grid-classic-item", html);
        Assert.Contains("gws-posts-grid-classic-img", html);
    }

    [Fact]
    public void Render_ShouldRenderOverlayLayout_ForPostsGridWidget()
    {
        var articles = new List<PublicArticleSummary>
        {
            new("first-post", "First Post", "First summary", "/media/first.jpg", DateTimeOffset.UtcNow)
        };

        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"posts-grid","props":{"layout":"overlay"}}"""),
            articles: articles);

        Assert.Contains("gws-posts-grid-overlay-item", html);
        Assert.Contains("gws-posts-grid-overlay-img", html);
        Assert.Contains("gws-posts-grid-overlay-scrim", html);
        Assert.DoesNotContain("gws-posts-grid-overlay-noimage", html);
    }

    [Fact]
    public void Render_ShouldFallBackToAPlainCard_ForOverlayLayout_WhenTheArticleHasNoHeroImage()
    {
        var articles = new List<PublicArticleSummary>
        {
            new("first-post", "First Post", "First summary", null, DateTimeOffset.UtcNow)
        };

        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"posts-grid","props":{"layout":"overlay"}}"""),
            articles: articles);

        Assert.Contains("gws-posts-grid-overlay-noimage", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Render_ShouldShowPublishDate_ForPostsGridWidget_WhenShowDateIsOnByDefault()
    {
        var publishedAt = new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero);
        var articles = new List<PublicArticleSummary> { new("first-post", "First Post", "", null, publishedAt) };

        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"posts-grid","props":{}}"""), articles: articles);

        Assert.Contains("gws-posts-grid-date", html);
        Assert.Contains("March 5, 2026", html);
    }

    [Fact]
    public void Render_ShouldHidePublishDate_ForPostsGridWidget_WhenShowDateIsOff()
    {
        var articles = new List<PublicArticleSummary> { new("first-post", "First Post", "", null, DateTimeOffset.UtcNow) };

        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"posts-grid","props":{"showDate":"false"}}"""),
            articles: articles);

        Assert.DoesNotContain("gws-posts-grid-date", html);
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("list")]
    [InlineData("classic")]
    [InlineData("overlay")]
    public void Render_PostsGridLayouts_ShouldHonorCountAndDateToggle(string presentation)
    {
        var articles = Enumerable.Range(1, 4)
            .Select(i => new PublicArticleSummary($"post-{i}", $"Post {i}", "Summary", null,
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))).ToList();
        var html = CmsBlockHtmlRenderer.Render(Layout($$$"""
            {"widgetType":"posts-grid","props":{"layout":"{{{presentation}}}","count":"2","showDate":"false"}}
            """), articles: articles);

        Assert.Contains("/blog/post-1", html);
        Assert.Contains("/blog/post-2", html);
        Assert.DoesNotContain("/blog/post-3", html);
        Assert.DoesNotContain("gws-posts-grid-date", html);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("classic")]
    public void Render_PostsGridLayouts_ShouldHonorImageAndExcerptToggles(string presentation)
    {
        var articles = new[] { new PublicArticleSummary("post", "Title", "Hidden summary", "/hidden.jpg", null) };
        var html = CmsBlockHtmlRenderer.Render(Layout($$$"""
            {"widgetType":"posts-grid","props":{"layout":"{{{presentation}}}","showImage":"false","showExcerpt":"false"}}
            """), articles: articles);

        Assert.Contains("Title", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("Hidden summary", html);
        Assert.DoesNotContain("gws-posts-grid-date", html);
    }

    [Fact]
    public void Render_PostsGrid_ShouldFallBackToGridForAnUnknownLayout()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"widgetType":"posts-grid","props":{"layout":"unknown"}}"""),
            articles: [new("post", "Title", "", null, null)]);

        Assert.Contains("gws-posts-grid-cols-3", html);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_RelatedPosts_ShouldPassDataThroughBothOverloadsAndLayoutModes(bool jsonOverload, bool freeform)
    {
        var layout = CmsBuilderJson.ParseLayout(Layout(
            """{"widgetType":"related-posts","props":{"sourceArticleSlug":"anchor"}}"""))!;
        if (freeform) layout.Sections[0].LayoutMode = CmsSectionLayoutModes.Freeform;
        var data = RelatedData(new RelatedArticleView("Related title", "related-slug", "/media/related.jpg"));

        var html = jsonOverload
            ? CmsBlockHtmlRenderer.Render(CmsBuilderJson.Serialize(layout), relatedPostsByAnchorSlug: data)
            : CmsBlockHtmlRenderer.Render(layout, relatedPostsByAnchorSlug: data);

        Assert.Contains("gws-related-posts", html);
        Assert.Contains("gws-related-posts-item", html);
        Assert.Contains("Related title", html);
        Assert.Contains("href=\"/blog/related-slug\"", html);
        Assert.Contains("src=\"/media/related.jpg\"", html);
    }

    [Theory]
    [InlineData("-1", 1)]
    [InlineData("0", 1)]
    [InlineData("4", 4)]
    [InlineData("6", 6)]
    [InlineData("99", 6)]
    [InlineData("invalid", 3)]
    public void Render_RelatedPosts_ShouldClampCount(string count, int expected)
    {
        var data = RelatedData(Enumerable.Range(1, 8).Select(i => new RelatedArticleView($"Title {i}", $"related-{i}")).ToArray());
        var html = CmsBlockHtmlRenderer.Render(Layout($$$"""
            {"widgetType":"related-posts","props":{"sourceArticleSlug":"anchor","count":"{{{count}}}"}}
            """), relatedPostsByAnchorSlug: data);

        for (var i = 1; i <= expected; i++) Assert.Contains($"/blog/related-{i}", html);
        Assert.DoesNotContain($"/blog/related-{expected + 1}", html);
    }

    [Theory]
    [InlineData("false", "/media/related.jpg")]
    [InlineData("true", null)]
    [InlineData("true", "")]
    public void Render_RelatedPosts_ShouldOmitDisabledOrMissingImages(string showImage, string? imageUrl)
    {
        var html = CmsBlockHtmlRenderer.Render(Layout($$$"""
            {"widgetType":"related-posts","props":{"sourceArticleSlug":"anchor","showImage":"{{{showImage}}}"}}
            """), relatedPostsByAnchorSlug: RelatedData(new RelatedArticleView("Title", "related", imageUrl)));

        Assert.Contains("/blog/related", html);
        Assert.DoesNotContain("<img", html);
    }

    [Theory]
    [InlineData("", true, "Pick a source article in the Inspector")]
    [InlineData("", false, "No related posts yet.")]
    [InlineData("missing", false, "No related posts yet.")]
    [InlineData("anchor", false, "No related posts yet.")]
    public void Render_RelatedPosts_ShouldExplainUnsetOrEmptyAnchors(string anchor, bool editMode, string message)
    {
        var html = CmsBlockHtmlRenderer.Render(Layout($$$"""
            {"widgetType":"related-posts","props":{"sourceArticleSlug":"{{{anchor}}}"}}
            """), editMode: editMode, relatedPostsByAnchorSlug: RelatedData());

        Assert.Contains(message, html);
    }

    [Fact]
    public void Render_RelatedPosts_ShouldUseEachWidgetsOwnAnchor()
    {
        var data = new Dictionary<string, IReadOnlyList<RelatedArticleView>>
        {
            ["first"] = [new("First recommendation", "first-result")],
            ["second"] = [new("Second recommendation", "second-result")]
        };
        var html = CmsBlockHtmlRenderer.Render(Layout("""
            {"widgetType":"related-posts","props":{"sourceArticleSlug":"first"}},
            {"widgetType":"related-posts","props":{"sourceArticleSlug":"second"}}
            """), relatedPostsByAnchorSlug: data);

        Assert.Contains("/blog/first-result", html);
        Assert.Contains("/blog/second-result", html);
    }

    [Fact]
    public void Render_RelatedPosts_ShouldEncodeTitlesAndAttributes()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"widgetType":"related-posts","props":{"sourceArticleSlug":"anchor"}}"""),
            relatedPostsByAnchorSlug: RelatedData(new RelatedArticleView("<script>unsafe</script>", "slug\" data-injected=\"yes", "/hero.jpg\" onerror=\"alert(1)")));

        Assert.Contains("&lt;script&gt;unsafe&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain(" data-injected=\"yes", html);
        Assert.DoesNotContain(" onerror=\"alert", html);
    }

    [Fact]
    public void RelatedPostsGuards_ShouldFindWidgetsAcrossSectionsAndColumnsAndDeduplicateAnchors()
    {
        var layout = CmsBuilderJson.ParseLayout("""
            {"sections":[
              {"columns":[
                {"widgets":[{"widgetType":"related-posts","props":{"sourceArticleSlug":"first"}}]},
                {"widgets":[{"widgetType":"related-posts","props":{"sourceArticleSlug":"FIRST"}}]}]},
              {"columns":[{"widgets":[
                {"widgetType":"related-posts","props":{"sourceArticleSlug":"second"}},
                {"widgetType":"related-posts","props":{"sourceArticleSlug":"  "}},
                {"widgetType":"posts-grid","props":{"sourceArticleSlug":"ignored"}}]}]}
            ]}
            """);

        Assert.True(CmsBlockHtmlRenderer.LayoutContainsRelatedPosts(layout));
        Assert.Equal(new[] { "first", "second" }, CmsBlockHtmlRenderer.GetRelatedPostsAnchorSlugs(layout));
    }

    [Fact]
    public void RelatedPostsGuards_ShouldHandleNullEmptyAndNonRelatedLayouts()
    {
        foreach (var layout in new PageLayout?[] { null, new(), CmsBuilderJson.ParseLayout(Layout("""{"widgetType":"posts-grid"}""")) })
        {
            Assert.False(CmsBlockHtmlRenderer.LayoutContainsRelatedPosts(layout));
            Assert.Empty(CmsBlockHtmlRenderer.GetRelatedPostsAnchorSlugs(layout));
        }
    }

    [Fact]
    public void PlainTextPreview_ShouldDescribeRelatedPostsWithoutMarkup()
    {
        var preview = CmsBlockHtmlRenderer.PlainTextPreview(new LayoutWidget
        {
            WidgetType = "related-posts", Props = new() { ["sourceArticleSlug"] = "anchor" }
        });

        Assert.Contains("related posts", preview);
        Assert.DoesNotContain("<", preview);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<RelatedArticleView>> RelatedData(params RelatedArticleView[] items) =>
        new Dictionary<string, IReadOnlyList<RelatedArticleView>> { ["anchor"] = items };

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

    // ── Workstream C, Tier 1 (booking embed) ─────────────────────────────────

    [Fact]
    public void Render_ShouldRenderBookingEmbed_WithIframeAndTitle()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"booking","props":{"bookingTypeSlug":"consult","title":"Book a consult","height":"800"}}"""));

        Assert.Contains("gws-booking-embed", html);
        Assert.Contains("src=\"/book/consult\"", html);
        Assert.Contains("Book a consult", html);
        Assert.Contains("height:800px", html);
    }

    [Fact]
    public void Render_ShouldRenderNothingForBookingEmbed_WhenNoSlugIsPicked()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"booking","props":{}}"""));

        Assert.DoesNotContain("gws-booking-embed", html);
        Assert.DoesNotContain("<iframe", html);
    }

    [Fact]
    public void Render_ShouldShowAPlaceholder_ForBookingEmbed_WhenNoSlugIsPicked_InEditMode()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"booking","props":{}}"""), editMode: true);

        Assert.Contains("gws-booking-embed-placeholder", html);
        Assert.Contains("Pick a booking type", html);
    }

    [Fact]
    public void Render_ShouldOmitTheBookingHeading_WhenTitleIsNotSet()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"booking","props":{"bookingTypeSlug":"consult"}}"""));

        Assert.DoesNotContain("gws-booking-embed-title", html);
    }

    [Fact]
    public void Render_ShouldClampBookingEmbedHeight_ToAReasonableRange()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"booking","props":{"bookingTypeSlug":"consult","height":"50"}}"""));

        Assert.Contains("height:300px", html);
    }

    [Fact]
    public void Render_ShouldEscapeTheBookingSlug_InTheIframeSrc()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"booking","props":{"bookingTypeSlug":"a slug\" onload=\"alert(1)"}}"""));

        Assert.DoesNotContain("onload=", html);
    }

    [Fact]
    public void PlainTextPreview_ShouldDescribeBookingWidget()
    {
        var widget = new LayoutWidget { WidgetType = "booking", Props = new() { ["bookingTypeSlug"] = "consult" } };

        Assert.Equal("[booking: consult]", CmsBlockHtmlRenderer.PlainTextPreview(widget));
    }

    // ── Workstream C, Tier 2 (stats/counter block) ──────────────────────────

    [Fact]
    public void Render_ShouldRenderStatsWidget_WithCounterTargetAndSuffixAndLabel()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"stats","props":{"itemsJson":"[{\"value\":\"500\",\"suffix\":\"+\",\"label\":\"Projects\"}]"}}"""));

        Assert.Contains("gws-stats", html);
        Assert.Contains("data-gws-counter-target=\"500\"", html);
        Assert.Contains(">0<", html);
        Assert.Contains("+", html);
        Assert.Contains("Projects", html);
    }

    [Fact]
    public void Render_ShouldRenderTheRealValueDirectly_ForStatsWidget_InEditMode()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"stats","props":{"itemsJson":"[{\"value\":\"500\",\"suffix\":\"+\",\"label\":\"Projects\"}]"}}"""),
            editMode: true);

        Assert.DoesNotContain("data-gws-counter-target", html);
        Assert.Contains(">500<", html);
    }

    [Fact]
    public void Render_ShouldRenderMultipleStatsItems_InOrder()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"stats","props":{"itemsJson":"[{\"value\":\"500\",\"suffix\":\"\",\"label\":\"First\"},{\"value\":\"99.9\",\"suffix\":\"%\",\"label\":\"Second\"}]"}}"""));

        var firstIndex = html.IndexOf("First", StringComparison.Ordinal);
        var secondIndex = html.IndexOf("Second", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex > firstIndex);
        Assert.Contains("data-gws-counter-target=\"99.9\"", html);
    }

    [Fact]
    public void Render_ShouldSkipItemsWithNoValue_ForStatsWidget()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"stats","props":{"itemsJson":"[{\"value\":\"\",\"suffix\":\"\",\"label\":\"Skipped\"},{\"value\":\"10\",\"suffix\":\"\",\"label\":\"Kept\"}]"}}"""));

        Assert.DoesNotContain("Skipped", html);
        Assert.Contains("Kept", html);
    }

    [Fact]
    public void Render_ShouldRenderNothingForStatsWidget_WithNoItems()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"stats","props":{"itemsJson":"[]"}}"""));

        Assert.DoesNotContain("gws-stats", html);
    }

    [Fact]
    public void PlainTextPreview_ShouldDescribeStatsWidget()
    {
        var widget = new LayoutWidget { WidgetType = "stats" };

        Assert.Equal("[stats]", CmsBlockHtmlRenderer.PlainTextPreview(widget));
    }

    [Fact]
    public void LayoutContainsStats_ShouldReturnTrue_WhenAWidgetIsStats()
    {
        var layout = CmsBuilderJson.ParseLayout(Layout("""{"id":"w1","widgetType":"stats","props":{}}"""));

        Assert.True(CmsBlockHtmlRenderer.LayoutContainsStats(layout));
    }

    [Fact]
    public void LayoutContainsStats_ShouldReturnFalse_WhenNoWidgetIsStats()
    {
        var layout = CmsBuilderJson.ParseLayout(Layout("""{"id":"w1","widgetType":"paragraph","props":{}}"""));

        Assert.False(CmsBlockHtmlRenderer.LayoutContainsStats(layout));
    }

    [Fact]
    public void LayoutContainsStats_ShouldReturnFalse_ForNullLayout()
    {
        Assert.False(CmsBlockHtmlRenderer.LayoutContainsStats(null));
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

    // ── Phase 4 (table-of-contents / reading-progress) ──────────────────────

    [Fact]
    public void Render_ShouldRenderTableOfContentsShell_WithTitleAndDataAttrs()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"table-of-contents","props":{"title":"In This Guide","minLevel":"h2","maxLevel":"h4","showNumbers":"true"}}"""));

        Assert.Contains("gws-toc", html);
        Assert.Contains("In This Guide", html);
        Assert.Contains("data-gws-toc=", html);
        Assert.Contains("&quot;minLevel&quot;:&quot;h2&quot;", html);
        Assert.Contains("&quot;maxLevel&quot;:&quot;h4&quot;", html);
        Assert.Contains("&quot;showNumbers&quot;:true", html);
        Assert.Contains("gws-toc-list", html);
    }

    [Fact]
    public void Render_ShouldDefaultTableOfContentsTitleAndLevels_WhenUnset()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"table-of-contents","props":{}}"""));

        Assert.Contains("On This Page", html);
        Assert.Contains("&quot;minLevel&quot;:&quot;h2&quot;", html);
        Assert.Contains("&quot;maxLevel&quot;:&quot;h3&quot;", html);
    }

    [Fact]
    public void Render_ShouldFallBackToDefaultLevels_ForAnUnrecognizedTableOfContentsLevel()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"table-of-contents","props":{"minLevel":"h1","maxLevel":"h9"}}"""));

        Assert.Contains("&quot;minLevel&quot;:&quot;h2&quot;", html);
        Assert.Contains("&quot;maxLevel&quot;:&quot;h3&quot;", html);
    }

    [Fact]
    public void PlainTextPreview_ShouldDescribeTableOfContents()
    {
        var widget = new LayoutWidget { WidgetType = "table-of-contents" };

        Assert.Equal("[table of contents]", CmsBlockHtmlRenderer.PlainTextPreview(widget));
    }

    [Fact]
    public void LayoutContainsTableOfContents_ShouldReturnTrue_WhenAWidgetIsTableOfContents()
    {
        var layout = CmsBuilderJson.ParseLayout(Layout("""{"id":"w1","widgetType":"table-of-contents","props":{}}"""));

        Assert.True(CmsBlockHtmlRenderer.LayoutContainsTableOfContents(layout));
    }

    [Fact]
    public void LayoutContainsTableOfContents_ShouldReturnFalse_WhenNoWidgetIsTableOfContents()
    {
        var layout = CmsBuilderJson.ParseLayout(Layout("""{"id":"w1","widgetType":"paragraph","props":{}}"""));

        Assert.False(CmsBlockHtmlRenderer.LayoutContainsTableOfContents(layout));
    }

    [Fact]
    public void LayoutContainsTableOfContents_ShouldReturnFalse_ForNullLayout()
    {
        Assert.False(CmsBlockHtmlRenderer.LayoutContainsTableOfContents(null));
    }

    [Fact]
    public void Render_ShouldRenderReadingProgressShell_WithHeightAndPosition()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"reading-progress","props":{"height":"6","position":"bottom"}}"""));

        Assert.Contains("gws-reading-progress-bottom", html);
        Assert.Contains("data-gws-reading-progress", html);
        Assert.Contains("--gws-reading-progress-height:6px", html);
        Assert.Contains("gws-reading-progress-bar", html);
    }

    [Fact]
    public void Render_ShouldDefaultReadingProgressToTop_WhenPositionIsUnrecognized()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"reading-progress","props":{"position":"sideways"}}"""));

        Assert.Contains("gws-reading-progress-top", html);
    }

    [Fact]
    public void Render_ShouldClampReadingProgressHeight_ToAReasonableRange()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"reading-progress","props":{"height":"999"}}"""));

        Assert.Contains("--gws-reading-progress-height:24px", html);
    }

    [Fact]
    public void Render_ShouldNotSetReadingProgressColor_WhenNeitherColorNorTokenIsSet()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"reading-progress","props":{}}"""));

        Assert.DoesNotContain("--gws-reading-progress-color", html);
    }

    [Fact]
    public void Render_ShouldResolveReadingProgressColor_FromRawHex()
    {
        var html = CmsBlockHtmlRenderer.Render(Layout(
            """{"id":"w1","widgetType":"reading-progress","props":{"color":"#ff0000"}}"""));

        Assert.Contains("--gws-reading-progress-color:#ff0000", html);
    }

    [Fact]
    public void Render_ShouldPreferReadingProgressColorToken_OverRawColor_WhenItResolves()
    {
        var tokens = new DesignTokenSet([new DesignToken("Accent", "#1c3d5a")], [], []);

        var html = CmsBlockHtmlRenderer.Render(
            Layout("""{"id":"w1","widgetType":"reading-progress","props":{"color":"#ff0000","colorToken":"Accent"}}"""),
            tokens: tokens);

        Assert.Contains("--gws-reading-progress-color:#1c3d5a", html);
        Assert.DoesNotContain("#ff0000", html);
    }

    [Fact]
    public void PlainTextPreview_ShouldDescribeReadingProgress()
    {
        var widget = new LayoutWidget { WidgetType = "reading-progress" };

        Assert.Equal("[reading progress bar]", CmsBlockHtmlRenderer.PlainTextPreview(widget));
    }

    [Fact]
    public void LayoutContainsReadingProgress_ShouldReturnTrue_WhenAWidgetIsReadingProgress()
    {
        var layout = CmsBuilderJson.ParseLayout(Layout("""{"id":"w1","widgetType":"reading-progress","props":{}}"""));

        Assert.True(CmsBlockHtmlRenderer.LayoutContainsReadingProgress(layout));
    }

    [Fact]
    public void LayoutContainsReadingProgress_ShouldReturnFalse_WhenNoWidgetIsReadingProgress()
    {
        var layout = CmsBuilderJson.ParseLayout(Layout("""{"id":"w1","widgetType":"paragraph","props":{}}"""));

        Assert.False(CmsBlockHtmlRenderer.LayoutContainsReadingProgress(layout));
    }

    [Fact]
    public void LayoutContainsReadingProgress_ShouldReturnFalse_ForNullLayout()
    {
        Assert.False(CmsBlockHtmlRenderer.LayoutContainsReadingProgress(null));
    }
}
