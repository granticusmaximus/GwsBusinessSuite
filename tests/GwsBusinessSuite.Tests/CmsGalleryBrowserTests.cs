using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Workstream C, Tier 3 (gallery/image grid) - BuildGalleryRuntimeScript builds the lightbox
// overlay dynamically and appends it to <body>, so proving click-to-open/close actually works
// needs a real browser, not just a static-shell unit test of the thumbnail markup.
[Collection("Playwright")]
public sealed class CmsGalleryBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static async Task<string> ReadCssAsync(string stylesheet)
    {
        var cssPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/GwsBusinessSuite.Web/wwwroot", stylesheet));
        return await File.ReadAllTextAsync(cssPath);
    }

    private static string BuildHtml(string css)
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
                                    WidgetType = "gallery",
                                    Props = new()
                                    {
                                        ["columns"] = "3",
                                        ["itemsJson"] = """[{"imageUrl":"/media/a.jpg","caption":"First photo"}]"""
                                    }
                                }
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
            <body>{bodyHtml}{CmsBlockHtmlRenderer.BuildGalleryRuntimeScript()}</body></html>
            """;
    }

    [Fact]
    public async Task Gallery_LightboxShouldBeClosed_BeforeAnyClick()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        (await page.Locator(".gws-gallery-lightbox").IsVisibleAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Gallery_ClickingAThumbnail_ShouldOpenTheLightboxWithTheRightImageAndCaption()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator(".gws-gallery-item").ClickAsync();

        (await page.Locator(".gws-gallery-lightbox").IsVisibleAsync()).Should().BeTrue();
        (await page.Locator(".gws-gallery-lightbox-img").GetAttributeAsync("src")).Should().Be("/media/a.jpg");
        (await page.Locator(".gws-gallery-lightbox-caption").InnerTextAsync()).Should().Be("First photo");
    }

    [Fact]
    public async Task Gallery_ClickingTheCloseButton_ShouldHideTheLightbox()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator(".gws-gallery-item").ClickAsync();
        await page.Locator(".gws-gallery-lightbox-close").ClickAsync();

        (await page.Locator(".gws-gallery-lightbox").IsVisibleAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Gallery_PressingEscape_ShouldHideTheLightbox()
    {
        var css = await ReadCssAsync("cms-public.css");
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync(BuildHtml(css));

        await page.Locator(".gws-gallery-item").ClickAsync();
        await page.Keyboard.PressAsync("Escape");

        (await page.Locator(".gws-gallery-lightbox").IsVisibleAsync()).Should().BeFalse();
    }
}
