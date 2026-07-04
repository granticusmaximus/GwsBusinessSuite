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
}
