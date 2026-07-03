using GwsBusinessSuite.Application.CmsBuilder;

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
    public void SubmittedBanner_ShouldRenderThanksMessage()
    {
        var html = PublicSiteHtmlRenderer.SubmittedBanner();

        Assert.Contains("Thanks", html);
    }
}
