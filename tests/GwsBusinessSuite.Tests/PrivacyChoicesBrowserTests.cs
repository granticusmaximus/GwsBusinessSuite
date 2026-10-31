using FluentAssertions;
using GwsBusinessSuite.Application.Growth;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Privacy choices on the public site (31-10-26, js/consent.js): the visit statistics and CJ's
// affiliate script wait for the visitor's OK where an opt-in is needed, and run as before elsewhere.
// The real consent.js and public-analytics.js run against a stubbed page and server.
[Collection("Playwright")]
public sealed class PrivacyChoicesBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static readonly string WebRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web/wwwroot"));
    private const string CjUrl = "https://www.anrdoezrs.net/am/101461368/include/allCj/impressions/page/am.js";
    private readonly List<string> _analytics = [];
    private int _cjLoads;

    [Fact]
    public async Task WhereAnOptInIsNeeded_NothingRunsUntilTheVisitorSays_AndNecessaryOnlyKeepsItThatWay()
    {
        await using var page = await OpenAsync(required: true);

        (await page.IsVisibleAsync(".gws-consent")).Should().BeTrue();
        await page.WaitForTimeoutAsync(300);
        _analytics.Should().BeEmpty("no visit is counted before the choice");
        (await page.EvaluateAsync<string?>("() => localStorage.getItem('gws_analytics_visitor_v2')")).Should().BeNull("no visitor id is stored");

        await page.ClickAsync("text=Necessary only");

        (await page.IsVisibleAsync(".gws-consent")).Should().BeFalse();
        await page.WaitForTimeoutAsync(300);
        _analytics.Should().BeEmpty();
        _cjLoads.Should().Be(0);
        (await page.EvaluateAsync<string>("() => localStorage.getItem('gws_consent_v1')")).Should().Contain("\"analytics\":false");
    }

    [Fact]
    public async Task AcceptAll_ShouldStartTheStatistics_AndLoadCj_AndBeRememberedNextVisit()
    {
        await using var page = await OpenAsync(required: true);

        await page.ClickAsync("text=Accept all");

        await WaitUntilAsync(() => _analytics.Count > 0 && _cjLoads == 1);
        _analytics[0].Should().Contain("\"eventName\":\"pageview\"");
        (await page.EvaluateAsync<string?>("() => localStorage.getItem('gws_analytics_visitor_v2')")).Should().NotBeNull();

        await page.ReloadAsync();
        await WaitUntilAsync(() => _analytics.Count > 1);
        (await page.IsVisibleAsync(".gws-consent")).Should().BeFalse("the choice is kept");
    }

    [Fact]
    public async Task Choose_ShouldAllowOneWithoutTheOther_AndPrivacyChoicesReopensIt()
    {
        await using var page = await OpenAsync(required: true);

        await page.ClickAsync("text=Choose");
        await page.CheckAsync("input[data-choice='analytics']");
        await page.ClickAsync("text=Save choices");

        await WaitUntilAsync(() => _analytics.Count > 0);
        _cjLoads.Should().Be(0, "affiliate partners weren't allowed");

        await page.ClickAsync("text=Privacy choices");
        (await page.IsVisibleAsync(".gws-consent")).Should().BeTrue();
    }

    [Fact]
    public async Task WhereNoOptInIsNeeded_ItRunsAsBefore_WithNoBanner()
    {
        await using var page = await OpenAsync(required: false);

        await WaitUntilAsync(() => _analytics.Count > 0 && _cjLoads == 1);
        (await page.IsVisibleAsync(".gws-consent")).Should().BeFalse();
    }

    [Theory]
    [InlineData("DE", true)]
    [InlineData("gb", true)]
    [InlineData("NO", true)]
    [InlineData("CH", true)]
    [InlineData("US", false)]
    [InlineData("CA", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void OptIn_ShouldBeAskedInTheEeaUkAndSwitzerland_AndWhereverWeCantTell(string? country, bool required) =>
        ConsentRegions.RequiresOptIn(country).Should().Be(required);

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(100);
        condition().Should().BeTrue();
    }

    private async Task<IPage> OpenAsync(bool required)
    {
        var page = await fixture.Browser.NewPageAsync();
        foreach (var (path, file) in new[] { ("/js/consent.js", "js/consent.js"), ("/public-analytics.js", "public-analytics.js") })
        {
            var source = await File.ReadAllTextAsync(Path.Combine(WebRoot, file));
            await page.RouteAsync("http://localhost" + path, route => route.FulfillAsync(new() { Status = 200, ContentType = "application/javascript", Body = source }));
        }
        await page.RouteAsync("http://localhost/api/consent/region", route =>
            route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = required ? """{"required":true}""" : """{"required":false}""" }));
        await page.RouteAsync("http://localhost/api/analytics/events", route =>
        {
            _analytics.Add(route.Request.PostData ?? string.Empty);
            return route.FulfillAsync(new() { Status = 202 });
        });
        await page.RouteAsync(CjUrl, route =>
        {
            _cjLoads++;
            return route.FulfillAsync(new() { Status = 200, ContentType = "application/javascript", Body = "" });
        });
        await page.RouteAsync("http://localhost/blog", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html",
            Body = $"""
                <!DOCTYPE html><html><head><title>Post</title></head>
                <body data-gws-affiliate-script="{CjUrl}"><p>Post</p>
                <p class="gws-privacy-choices"><a href="#privacy-choices" data-gws-consent-open>Privacy choices</a></p>
                <script src="/js/consent.js" defer></script><script src="/public-analytics.js" defer></script></body></html>
                """
        }));
        await page.GotoAsync("http://localhost/blog");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return page;
    }
}
