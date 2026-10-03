using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

// Served pages can't run inline scripts (CSP script-src has no 'unsafe-inline'), so widget
// runtimes are loaded from /js/cms-runtime/{name}.js - these pin that contract.
public sealed class CmsRuntimeScriptsTests
{
    [Fact]
    public void EveryRuntime_IsServableJavaScript_WithoutItsScriptWrapper()
    {
        foreach (var name in CmsBlockHtmlRenderer.RuntimeScripts.Keys)
        {
            var source = CmsBlockHtmlRenderer.RuntimeScriptSource(name);
            Assert.NotNull(source);
            Assert.DoesNotContain("<script", source);
            Assert.DoesNotContain("</script>", source);
            Assert.StartsWith("(function", source.TrimStart());
        }

        Assert.Null(CmsBlockHtmlRenderer.RuntimeScriptSource("not-a-runtime"));
    }

    [Fact]
    public void BuildRuntimeScriptTags_EmitsExternalTagsOnly_ForTheRuntimesThePageUses()
    {
        var layout = CmsBuilderJson.ParseLayout(
            """{"sections":[{"id":"s1","columns":[{"id":"c1","widgets":[{"id":"w1","widgetType":"tabs","props":{}}]}]}]}""");

        var tags = CmsBlockHtmlRenderer.BuildRuntimeScriptTags(layout, "<div data-gws-interaction=\"{}\"></div>");

        Assert.Contains("<script src=\"/js/cms-runtime/tabs.js?v=", tags);
        Assert.Contains("<script src=\"/js/cms-runtime/interactions.js?v=", tags);
        Assert.DoesNotContain("gallery.js", tags);
        Assert.DoesNotContain("<script>", tags);
    }

    [Fact]
    public void LegacyHomePage_LosesOnlyItsDeadInlineBlogGridScript()
    {
        var legacyHtml = "<div class=\"home-blog-grid\" data-home-blog-grid></div>\n" + GrantWatsonHomepageTemplate.BlogGridRuntimeScript;
        var layout = new PageLayout();
        layout.Sections.Add(new LayoutSection { Columns = [new LayoutColumn { Widgets = [new LayoutWidget { WidgetType = "html", Props = new() { ["content"] = legacyHtml } }] }] });
        var json = CmsBuilderJson.Serialize(layout);

        Assert.True(GrantWatsonHomepageTemplate.TryRemoveLegacyInlineScript(json, out var cleaned));

        var content = CmsBuilderJson.ParseLayout(cleaned)!.Sections[0].Columns[0].Widgets[0].Props["content"];
        Assert.Contains("data-home-blog-grid", content);
        Assert.DoesNotContain("<script", content);
        Assert.False(GrantWatsonHomepageTemplate.TryRemoveLegacyInlineScript(cleaned, out _));
        var seeded = CmsBuilderJson.ParseLayout(GrantWatsonHomepageTemplate.CreateBlocksJson())!;
        Assert.DoesNotContain(seeded.Sections.SelectMany(s => s.Columns).SelectMany(c => c.Widgets),
            w => w.Props.Values.Any(v => v.Contains("<script", StringComparison.OrdinalIgnoreCase)));
    }
}
