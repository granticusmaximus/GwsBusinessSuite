using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Appearance > Menus sub-links: rendering, and the dropdown's behaviour in a real browser.
public sealed class NavMenuSubLinkTests
{
    private static readonly IReadOnlyList<NavMenuItem> Menu =
    [
        new("1", "Blog", "/blog", false),
        new("2", "Services", "/services", false,
            [new("2a", "Websites", "/services/websites", false), new("2b", "Apps & <APIs>", "/services/apps", false)]),
        new("3", "Resources", "", false, [new("3a", "Guides", "/guides", false)])
    ];

    [Fact]
    public void ParseNavItems_ShouldReadMenusWithAndWithoutSubLinks()
    {
        var old = PublicSiteHtmlRenderer.ParseNavItems("""[{"id":"1","label":"Blog","href":"/blog","openInNewTab":false}]""");
        old.Should().ContainSingle().Which.HasChildren.Should().BeFalse();

        var nested = PublicSiteHtmlRenderer.ParseNavItems(
            """[{"id":"2","label":"Services","href":"/services","openInNewTab":false,"children":[{"id":"2a","label":"Websites","href":"/w","openInNewTab":true}]}]""");
        nested.Single().Children!.Single().Should().Be(new NavMenuItem("2a", "Websites", "/w", true));

        System.Text.Json.JsonSerializer.Serialize(Menu).Should().NotContain("HasChildren", "a computed flag isn't saved");
    }

    [Fact]
    public void Header_ShouldRenderDropdowns_AndFooter_Groups()
    {
        var html = PublicSiteHtmlRenderer.Layout("T", "D", null, "<p>x</p>", navItems: Menu, footerNavItems: Menu);

        html.Should().Contain("<a href=\"/blog\">Blog</a>");
        // A parent with its own page keeps the link, plus a separate menu button.
        html.Should().Contain("<div class=\"nav-dropdown\"><a class=\"nav-dropdown-link\" href=\"/services\">Services</a>"
            + "<button type=\"button\" class=\"nav-dropdown-toggle\" data-gws-dropdown aria-expanded=\"false\" aria-label=\"Services menu\">");
        html.Should().Contain("<div class=\"nav-dropdown-menu\"><a href=\"/services/websites\">Websites</a><a href=\"/services/apps\">Apps &amp; &lt;APIs&gt;</a></div>");
        // A parent without a page is just the button.
        html.Should().Contain("aria-expanded=\"false\">Resources <i class=\"bi bi-chevron-down\"");
        html.Should().Contain("<div class=\"footer-group\"><a class=\"footer-group-heading\" href=\"/services\">Services</a><a href=\"/services/websites\">");
        html.Should().Contain("<div class=\"footer-group\"><span class=\"footer-group-heading\">Resources</span><a href=\"/guides\">Guides</a></div>");
    }

    [Fact]
    public void CustomHeaderMenus_ShouldGetTheSameDropdowns()
    {
        var filled = PublicSiteHtmlRenderer.FillSiteChrome(
            """<nav class="gws-nav-menu" data-gws-nav-menu="header"></nav><nav class="gws-nav-menu" data-gws-nav-menu="footer"></nav>""",
            Menu, Menu, "Site", null);

        filled.Should().Contain("class=\"nav-dropdown\"").And.Contain("class=\"footer-group\"");
    }

    [Collection("Playwright")]
    public sealed class InTheBrowser(PlaywrightBrowserFixture fixture)
    {
        private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));

        [Fact]
        public async Task Dropdown_ShouldOpenOnHoverAndClick_AndCloseOnEscape()
        {
            await using var page = await OpenAsync(1280);
            var menu = page.Locator(".nav-dropdown").First.Locator(".nav-dropdown-menu");
            var toggle = page.Locator(".nav-dropdown").First.Locator("[data-gws-dropdown]");

            (await menu.IsVisibleAsync()).Should().BeFalse("closed until asked");
            await page.HoverAsync("text=Services");
            (await menu.IsVisibleAsync()).Should().BeTrue("opens on hover");
            await page.Mouse.MoveAsync(5, 600);
            (await menu.IsVisibleAsync()).Should().BeFalse();

            await toggle.ClickAsync();
            await page.Mouse.MoveAsync(5, 600); // a click leaves the pointer hovering, which also opens it
            (await toggle.GetAttributeAsync("aria-expanded")).Should().Be("true");
            (await menu.IsVisibleAsync()).Should().BeTrue();
            await page.Keyboard.PressAsync("Escape");
            (await menu.IsVisibleAsync()).Should().BeFalse("Escape closes it even while focused");
            (await toggle.GetAttributeAsync("aria-expanded")).Should().Be("false");
            (await page.EvaluateAsync<bool>("() => document.activeElement.matches('[data-gws-dropdown]')")).Should().BeTrue("focus returns to the button");

            // Closed: Tab skips its sub-links to the next top-level item, as a collapsed menu should.
            await page.Keyboard.PressAsync("Tab");
            (await page.EvaluateAsync<string>("() => document.activeElement.textContent.trim()")).Should().Be("Resources");
            // Coming back to the button opens it again, and Tab then goes into the sub-links.
            await page.Keyboard.PressAsync("Shift+Tab");
            (await menu.IsVisibleAsync()).Should().BeTrue("keyboard focus opens it again");
            (await page.EvaluateAsync<bool>("() => document.activeElement.matches('[data-gws-dropdown]')")).Should().BeTrue();
            await page.Keyboard.PressAsync("Tab");
            (await page.EvaluateAsync<string>("() => document.activeElement.textContent")).Should().Be("Websites");

            await page.Mouse.ClickAsync(5, 600);
            (await menu.IsVisibleAsync()).Should().BeFalse("a click elsewhere closes it");
        }

        [Fact]
        public async Task Phone_ShouldListSubLinksUnderTheirParent()
        {
            await using var page = await OpenAsync(390);
            await page.ClickAsync("[data-gws-menu-toggle]");

            var subLink = page.Locator(".nav-links a[href='/services/websites']");
            (await subLink.IsVisibleAsync()).Should().BeTrue("shown without hovering on a phone");
            var parentBox = await page.Locator(".nav-dropdown-link").First.BoundingBoxAsync();
            var childBox = await subLink.BoundingBoxAsync();
            childBox!.X.Should().BeGreaterThan(parentBox!.X, "indented under its parent");
            childBox.Y.Should().BeGreaterThan(parentBox.Y);
            (await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - window.innerWidth")).Should().BeLessThanOrEqualTo(0);
        }

        private async Task<IPage> OpenAsync(int width)
        {
            var page = await fixture.Browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 800 } });
            var html = PublicSiteHtmlRenderer.Layout("Menus", "D", null, "<main style=\"height:1200px\">Body</main>", navItems: Menu);
            await page.RouteAsync("**/*", async route =>
            {
                var url = new Uri(route.Request.Url);
                if (url.Host != "localhost") { await route.AbortAsync(); return; }
                if (url.AbsolutePath == "/") { await route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = html }); return; }
                var file = Path.Combine(WebRoot, url.AbsolutePath.TrimStart('/'));
                if (!File.Exists(file)) { await route.FulfillAsync(new() { Status = 404 }); return; }
                await route.FulfillAsync(new()
                {
                    Status = 200,
                    ContentType = file.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "application/javascript",
                    Body = await File.ReadAllTextAsync(file)
                });
            });
            await page.GotoAsync("http://localhost/");
            return page;
        }
    }
}
