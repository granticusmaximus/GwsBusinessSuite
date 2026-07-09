using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

public sealed class PublicSiteHtmlRendererTests
{
    [Fact]
    public void ParseNavItems_ShouldReturnDefaultList_WhenJsonIsEmpty()
    {
        var items = PublicSiteHtmlRenderer.ParseNavItems(null);

        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.Label == "About");
        Assert.Contains(items, i => i.Label == "Blog");
        Assert.Contains(items, i => i.Label == "Contact");
    }

    [Fact]
    public void ParseNavItems_ShouldReturnDefaultList_WhenJsonIsInvalid()
    {
        var items = PublicSiteHtmlRenderer.ParseNavItems("not json");

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void ParseNavItems_ShouldReturnDefaultList_WhenArrayIsEmpty()
    {
        var items = PublicSiteHtmlRenderer.ParseNavItems("[]");

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void ParseNavItems_ShouldReturnStoredItems_InOrder()
    {
        var json = """[{"id":"1","label":"Services","href":"/services","openInNewTab":false},{"id":"2","label":"GitHub","href":"https://github.com","openInNewTab":true}]""";

        var items = PublicSiteHtmlRenderer.ParseNavItems(json);

        Assert.Equal(2, items.Count);
        Assert.Equal("Services", items[0].Label);
        Assert.Equal("/services", items[0].Href);
        Assert.False(items[0].OpenInNewTab);
        Assert.Equal("GitHub", items[1].Label);
        Assert.True(items[1].OpenInNewTab);
    }

    [Fact]
    public void Layout_ShouldRenderProvidedNavItems_NotTheDefaultOnes()
    {
        var items = new List<NavMenuItem> { new("1", "Services", "/services", false) };

        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>", items);

        Assert.Contains("Services", html);
        Assert.Contains("href=\"/services\"", html);
        Assert.DoesNotContain(">About<", html);
    }

    [Fact]
    public void Layout_ShouldOpenInNewTab_WhenConfigured()
    {
        var items = new List<NavMenuItem> { new("1", "GitHub", "https://github.com", true) };

        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>", items);

        Assert.Contains("target=\"_blank\"", html);
    }

    [Fact]
    public void Layout_ShouldFallBackToDefaultNav_WhenNavItemsOmitted()
    {
        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>");

        Assert.Contains(">About<", html);
        Assert.Contains(">Blog<", html);
        Assert.Contains(">Contact<", html);
    }

    [Fact]
    public void Layout_ShouldFallBackToDefaultAccentAndFontPairing_WhenTokensOmitted()
    {
        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>");

        Assert.Contains("--accent: #f59e0b;", html);
        Assert.Contains("'Playfair Display'", html);
        Assert.Contains("'Inter'", html);
    }

    [Fact]
    public void Layout_ShouldRenderCustomAccentColor()
    {
        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>", accentColorHex: "#2563eb");

        Assert.Contains("--accent: #2563eb;", html);
        Assert.Contains("color-mix(in srgb, #2563eb 12%, transparent)", html);
        Assert.Contains("color-mix(in srgb, #2563eb 85%, black)", html);
    }

    [Fact]
    public void Layout_ShouldRenderCanonicalAndCustomFavicon_WhenProvided()
    {
        var html = PublicSiteHtmlRenderer.Layout(
            "Title",
            "Description",
            null,
            "<p>body</p>",
            canonicalUrl: "https://example.com/page",
            faviconUrl: "https://cdn.example.com/favicon.png");

        Assert.Contains("rel=\"canonical\"", html);
        Assert.Contains("https://example.com/page", html);
        Assert.Contains("https://cdn.example.com/favicon.png", html);
    }

    [Fact]
    public void Layout_ShouldRenderCustomLogo_WhenProvided()
    {
        var html = PublicSiteHtmlRenderer.Layout(
            "Title",
            "Description",
            null,
            "<p>body</p>",
            siteName: "Example Site",
            logoUrl: "https://cdn.example.com/logo.svg");

        Assert.Contains("site-logo-custom", html);
        Assert.Contains("https://cdn.example.com/logo.svg", html);
        Assert.Contains("alt=\"Example Site\"", html);
    }

    [Fact]
    public void Layout_ShouldRenderModernFontPairing()
    {
        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>", fontPairingKey: CmsFontPairings.Modern);

        Assert.Contains("'Manrope'", html);
        Assert.Contains("Manrope:wght", html);
    }

    [Fact]
    public void Layout_ShouldFallBackToElegantPairing_WhenKeyIsUnrecognized()
    {
        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>", fontPairingKey: "not-a-real-key");

        Assert.Contains("'Playfair Display'", html);
    }

    [Fact]
    public void ParseFooterNavItems_ShouldReturnEmptyList_WhenJsonIsEmpty()
    {
        var items = PublicSiteHtmlRenderer.ParseFooterNavItems(null);

        Assert.Empty(items);
    }

    [Fact]
    public void ParseFooterNavItems_ShouldReturnEmptyList_WhenJsonIsInvalid()
    {
        var items = PublicSiteHtmlRenderer.ParseFooterNavItems("not json");

        Assert.Empty(items);
    }

    [Fact]
    public void ParseFooterNavItems_ShouldReturnEmptyList_WhenArrayIsEmpty()
    {
        var items = PublicSiteHtmlRenderer.ParseFooterNavItems("[]");

        Assert.Empty(items);
    }

    [Fact]
    public void ParseFooterNavItems_ShouldReturnStoredItems_InOrder()
    {
        var json = """[{"id":"1","label":"Privacy","href":"/privacy","openInNewTab":false},{"id":"2","label":"Terms","href":"/terms","openInNewTab":false}]""";

        var items = PublicSiteHtmlRenderer.ParseFooterNavItems(json);

        Assert.Equal(2, items.Count);
        Assert.Equal("Privacy", items[0].Label);
        Assert.Equal("Terms", items[1].Label);
    }

    [Fact]
    public void Layout_ShouldRenderFooterNavItems_WhenProvided()
    {
        var footerItems = new List<NavMenuItem> { new("1", "Privacy", "/privacy", false) };

        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>", footerNavItems: footerItems);

        Assert.Contains("footer-links", html);
        Assert.Contains("Privacy", html);
        Assert.Contains("href=\"/privacy\"", html);
    }

    [Fact]
    public void Layout_ShouldOmitFooterLinksSection_WhenFooterNavItemsEmptyOrOmitted()
    {
        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>");

        Assert.DoesNotContain("footer-links", html);
    }

    [Theory]
    [InlineData("https://github.com/granticusmaximus", "bi-github")]
    [InlineData("https://www.github.com/granticusmaximus", "bi-github")]
    [InlineData("https://x.com/grantwatson", "bi-twitter-x")]
    [InlineData("https://twitter.com/grantwatson", "bi-twitter-x")]
    [InlineData("https://www.linkedin.com/in/grantwatson", "bi-linkedin")]
    [InlineData("https://www.facebook.com/grantwatson", "bi-facebook")]
    [InlineData("https://www.instagram.com/grantwatson", "bi-instagram")]
    [InlineData("https://www.youtube.com/@grantwatson", "bi-youtube")]
    [InlineData("https://www.tiktok.com/@grantwatson", "bi-tiktok")]
    [InlineData("https://www.reddit.com/user/grantwatson", "bi-reddit")]
    public void Layout_ShouldRenderSocialIcon_InsteadOfLabelText_ForKnownPlatforms(string href, string expectedIconClass)
    {
        var items = new List<NavMenuItem> { new("1", "My Profile", href, true) };

        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>", items);

        Assert.Contains(expectedIconClass, html);
        Assert.Contains("aria-label=\"My Profile\"", html);
        Assert.Contains("nav-link-social", html);
        // The label text itself must not appear as visible link content - only as the
        // accessible name via aria-label - since the whole point is icon instead of text.
        Assert.DoesNotContain(">My Profile<", html);
    }

    [Fact]
    public void Layout_ShouldRenderDevToIcon_AsInlineSvg_NotBootstrapIconClass()
    {
        var items = new List<NavMenuItem> { new("1", "Dev.to", "https://dev.to/grantwatson", true) };

        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>", items);

        Assert.Contains("<svg", html);
        Assert.Contains("nav-link-social", html);
        Assert.Contains("aria-label=\"Dev.to\"", html);
    }

    [Fact]
    public void Layout_ShouldRenderPlainTextLink_ForNonSocialUrls()
    {
        var items = new List<NavMenuItem> { new("1", "Services", "/services", false) };

        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>", items);

        Assert.Contains(">Services<", html);
        Assert.DoesNotContain("nav-link-social", html);
    }

    [Fact]
    public void Layout_ShouldRenderSocialIcon_InFooterLinksToo()
    {
        var footerItems = new List<NavMenuItem> { new("1", "GitHub", "https://github.com/granticusmaximus", true) };

        var html = PublicSiteHtmlRenderer.Layout("Title", "Description", null, "<p>body</p>", footerNavItems: footerItems);

        Assert.Contains("footer-links", html);
        Assert.Contains("bi-github", html);
        Assert.Contains("aria-label=\"GitHub\"", html);
    }

    [Fact]
    public void SubmittedBanner_ShouldRenderThanksMessage()
    {
        var html = PublicSiteHtmlRenderer.SubmittedBanner();

        Assert.Contains("Thanks", html);
    }

    [Fact]
    public void BlogListBody_ShouldRenderCategoryAndTagPills_OnArticleCards_WhenPresent()
    {
        var article = new PublicSiteHtmlRenderer.ArticleSummary(
            "my-post", "My Post", "Description", "", "5 min", DateTimeOffset.UtcNow, null,
            "Dev Tools", "dev-tools", ["dotnet", "blazor"]);

        var html = PublicSiteHtmlRenderer.BlogListBody(
            [article], [], null, [], null, null, page: 1, pageSize: 10, totalCount: 1, totalPages: 1);

        Assert.Contains("Dev Tools", html);
        Assert.Contains("dotnet", html);
        Assert.Contains("blazor", html);
    }

    [Fact]
    public void BlogListBody_ShouldOmitCategoryAndTagPills_WhenAbsent()
    {
        var article = new PublicSiteHtmlRenderer.ArticleSummary(
            "my-post", "My Post", "Description", "", "5 min", DateTimeOffset.UtcNow, null,
            null, null, []);

        var html = PublicSiteHtmlRenderer.BlogListBody(
            [article], [], null, [], null, null, page: 1, pageSize: 10, totalCount: 1, totalPages: 1);

        Assert.DoesNotContain("Dev Tools", html);
    }

    [Fact]
    public void BlogListBody_ShouldRenderCategoryFilterCloud_WhenCategoriesProvided()
    {
        var categories = new List<PublicSiteHtmlRenderer.CategorySummary>
        {
            new("Dev Tools", "dev-tools"),
            new("Tutorials", "tutorials")
        };
        var article = new PublicSiteHtmlRenderer.ArticleSummary(
            "my-post", "My Post", "Description", "", "5 min", DateTimeOffset.UtcNow, null, null, null, []);

        var html = PublicSiteHtmlRenderer.BlogListBody(
            [article], [], null, categories, "dev-tools", null, page: 1, pageSize: 10, totalCount: 1, totalPages: 1);

        Assert.Contains("Dev Tools", html);
        Assert.Contains("Tutorials", html);
        Assert.Contains("category=dev-tools", html);
    }

    [Fact]
    public void BlogPostBody_ShouldRenderClickableCategoryAndTagPills_WhenPresent()
    {
        var html = PublicSiteHtmlRenderer.BlogPostBody(
            "Title", "Description", "Grant Watson", DateTimeOffset.UtcNow, "5 min", "",
            null, "", "", "Body text",
            categoryName: "Dev Tools", categorySlug: "dev-tools", tags: ["dotnet", "blazor"]);

        Assert.Contains("Dev Tools", html);
        Assert.Contains("href=\"/blog?category=dev-tools\"", html);
        Assert.Contains("href=\"/blog?tag=dotnet\"", html);
        Assert.Contains("href=\"/blog?tag=blazor\"", html);
    }

    [Fact]
    public void BlogPostBody_ShouldOmitCategoryAndTagPills_WhenNotProvided()
    {
        var html = PublicSiteHtmlRenderer.BlogPostBody(
            "Title", "Description", "Grant Watson", DateTimeOffset.UtcNow, "5 min", "",
            null, "", "", "Body text");

        Assert.DoesNotContain("/blog?category=", html);
        Assert.DoesNotContain("/blog?tag=", html);
    }

    [Fact]
    public void BlogPostBody_ShouldRenderNestedRepliesAndReplyFormContext()
    {
        var comments = new[]
        {
            new GwsBusinessSuite.Application.Comments.CommentView
            {
                Id = Guid.NewGuid(),
                AuthorName = "Ada",
                Body = "Parent",
                CreatedAt = DateTimeOffset.UtcNow,
                Replies =
                [
                    new GwsBusinessSuite.Application.Comments.CommentView
                    {
                        Id = Guid.NewGuid(),
                        ParentCommentId = Guid.NewGuid(),
                        AuthorName = "Bob",
                        Body = "Reply",
                        CreatedAt = DateTimeOffset.UtcNow,
                        Depth = 1
                    }
                ]
            }
        };

        var html = PublicSiteHtmlRenderer.BlogPostBody(
            "Title",
            "Description",
            "Grant Watson",
            DateTimeOffset.UtcNow,
            "5 min",
            "",
            null,
            "",
            "",
            "Body text",
            slug: "post",
            approvedComments: comments,
            replyToCommentId: comments[0].Id,
            replyToAuthorName: comments[0].AuthorName);

        Assert.Contains("comment-item-replies", html);
        Assert.Contains("Replying to <strong>Ada</strong>", html);
        Assert.Contains("name=\"parentCommentId\"", html);
        Assert.Contains("?replyTo=", html);
    }

    [Fact]
    public void SitemapXml_ShouldRenderEntriesWithLastModified()
    {
        var xml = PublicSiteHtmlRenderer.SitemapXml(
        [
            new PublicSiteHtmlRenderer.SitemapEntry("https://example.com/", new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero))
        ]);

        Assert.Contains("<urlset", xml);
        Assert.Contains("https://example.com/", xml);
        Assert.Contains("<lastmod>2026-07-09</lastmod>", xml);
    }

    [Fact]
    public void RobotsTxt_ShouldIncludeAdminDisallowAndSitemap()
    {
        var robots = PublicSiteHtmlRenderer.RobotsTxt("https://example.com/sitemap.xml");

        Assert.Contains("Disallow: /admin", robots);
        Assert.Contains("Sitemap: https://example.com/sitemap.xml", robots);
    }

    [Fact]
    public void RssXml_ShouldRenderChannelAndItemMetadata()
    {
        var xml = PublicSiteHtmlRenderer.RssXml(
            "Example Blog",
            "Description",
            "https://example.com/blog",
            [
                new PublicSiteHtmlRenderer.RssItem(
                    "Launch Post",
                    "https://example.com/blog/launch-post",
                    "Post description",
                    new DateTimeOffset(2026, 7, 9, 14, 30, 0, TimeSpan.Zero),
                    "https://example.com/blog/launch-post")
            ]);

        Assert.Contains("<rss version=\"2.0\">", xml);
        Assert.Contains("<title>Launch Post</title>", xml);
        Assert.Contains("<link>https://example.com/blog/launch-post</link>", xml);
        Assert.Contains("<description>Post description</description>", xml);
    }
}
