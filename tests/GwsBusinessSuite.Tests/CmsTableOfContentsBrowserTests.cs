using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Phase 4 (table-of-contents widget) - BuildTableOfContentsRuntimeScript is a client-side DOM
// scan (decision 4 in the master plan), so unlike the widget's own static-shell unit tests
// (CmsBlockHtmlRendererTests), this needs a real browser to prove the scan actually finds the
// page's headings, builds working anchor links, and scrollspies the active one.
[Collection("Playwright")]
public sealed class CmsTableOfContentsBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string BuildHtml(string cssPath, string css)
    {
        var layout = new PageLayout
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
                                    WidgetType = "table-of-contents",
                                    Props = new() { ["title"] = "On This Page", ["minLevel"] = "h2", ["maxLevel"] = "h3" }
                                },
                                new LayoutWidget { WidgetType = "heading", Props = new() { ["level"] = "h2", ["text"] = "First Section" } },
                                new LayoutWidget { WidgetType = "spacer", Props = new() { ["height"] = "1400" } },
                                new LayoutWidget { WidgetType = "heading", Props = new() { ["level"] = "h3", ["text"] = "First Subsection" } },
                                new LayoutWidget { WidgetType = "spacer", Props = new() { ["height"] = "1400" } },
                                new LayoutWidget { WidgetType = "heading", Props = new() { ["level"] = "h2", ["text"] = "Second Section" } },
                                new LayoutWidget { WidgetType = "spacer", Props = new() { ["height"] = "1400" } }
                            ]
                        }
                    ]
                }
            ]
        };

        var bodyHtml = CmsBlockHtmlRenderer.Render(layout);
        return $"""
            <!doctype html>
            <html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>{css}</style></head>
            <body>{bodyHtml}{CmsBlockHtmlRenderer.BuildTableOfContentsRuntimeScript()}</body></html>
            """;
    }

    private static async Task<string> ReadCssAsync(string stylesheet)
    {
        var cssPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot", stylesheet));
        return await File.ReadAllTextAsync(cssPath);
    }

    [Fact]
    public async Task Toc_ShouldListEveryMatchingHeading_InDocumentOrder()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        await page.SetContentAsync(BuildHtml("cms-public.css", css));

        var items = page.Locator(".gws-toc-item");
        (await items.CountAsync()).Should().Be(3);
        (await items.Nth(0).InnerTextAsync()).Should().Contain("First Section");
        (await items.Nth(1).InnerTextAsync()).Should().Contain("First Subsection");
        (await items.Nth(2).InnerTextAsync()).Should().Contain("Second Section");
    }

    [Fact]
    public async Task Toc_ClickingALink_ShouldScrollTheMatchingHeadingIntoView()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        await page.SetContentAsync(BuildHtml("cms-public.css", css));

        await page.Locator(".gws-toc-item").Nth(2).Locator("a").ClickAsync();
        await page.WaitForTimeoutAsync(150);

        var heading = page.Locator("h2:has-text('Second Section')");
        var box = await heading.BoundingBoxAsync();
        box.Should().NotBeNull();
        box!.Y.Should().BeInRange(-5, 805, "clicking a TOC link should bring its heading into (or very near) the viewport");
    }

    [Fact]
    public async Task Toc_Scrollspy_ShouldMarkTheCurrentSectionsLinkActive()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        await page.SetContentAsync(BuildHtml("cms-public.css", css));

        var secondHeadingId = await page.Locator("h2:has-text('Second Section')").GetAttributeAsync("id");
        secondHeadingId.Should().NotBeNullOrWhiteSpace();

        await page.EvaluateAsync("id => document.getElementById(id).scrollIntoView()", secondHeadingId);
        await page.WaitForTimeoutAsync(300);

        var activeLink = page.Locator(".gws-toc-item a.is-active");
        (await activeLink.CountAsync()).Should().Be(1);
        (await activeLink.InnerTextAsync()).Should().Contain("Second Section");
    }

    [Fact]
    public async Task Toc_ShouldHideItself_WhenThePageHasNoMatchingHeadings()
    {
        var layout = new PageLayout
        {
            Sections =
            [
                new LayoutSection
                {
                    Columns =
                    [
                        new LayoutColumn
                        {
                            Widgets = [new LayoutWidget { WidgetType = "table-of-contents", Props = new() { ["minLevel"] = "h2", ["maxLevel"] = "h3" } }]
                        }
                    ]
                }
            ]
        };
        var bodyHtml = CmsBlockHtmlRenderer.Render(layout);
        var html = $"""
            <!doctype html><html><head></head>
            <body>{bodyHtml}{CmsBlockHtmlRenderer.BuildTableOfContentsRuntimeScript()}</body></html>
            """;

        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(html);
        await page.WaitForTimeoutAsync(50);

        (await page.Locator(".gws-toc").IsVisibleAsync()).Should().BeFalse();
    }
}
