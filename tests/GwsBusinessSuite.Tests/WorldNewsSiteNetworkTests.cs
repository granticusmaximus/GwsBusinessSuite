using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// B5: shared tracking IDs join sites into one network; RDAP gives registration dates.
[Collection("SourceIndependenceStatics")]
public sealed class WorldNewsSiteNetworkTests
{
    [Fact]
    public void Ids_ShouldReadAnalyticsTagManagerAdSenseAndPixelAccounts()
    {
        const string html = """
            <script async src="https://www.googletagmanager.com/gtag/js?id=G-ABC123XYZ"></script>
            <script>gtag('config', 'UA-1234567-2'); (window,document,'script','dataLayer','GTM-K9P2QX');</script>
            <script async src="https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=ca-pub-1234567890123456"></script>
            <script>fbq('init', '987654321012345');</script>
            <p>G-SHOCK watches are popular.</p>
            """;

        Assert.Equal(["FB-987654321012345", "G-ABC123XYZ", "GTM-K9P2QX", "PUB-1234567890123456", "UA-1234567-2"], SiteNetworks.Ids(html));
    }

    [Fact]
    public void Find_ShouldGroupOutletsSharingAnId_AndIgnoreVendorIdsOnManySites()
    {
        var signals = new List<SiteSignal>
        {
            new("truth-daily.example", "Truth Daily", ["GTM-AAAA1"], null),
            new("patriot-wire.example", "Patriot Wire", ["GTM-AAAA1", "PUB-111"], null),
            new("free-voice.example", "Free Voice", ["PUB-111"], null),
            new("honest.example", "Honest Paper", ["GTM-BBBB2"], null),
        };
        signals.AddRange(Enumerable.Range(0, SiteNetworks.MaxSitesPerId).Select(i => new SiteSignal($"s{i}.example", $"Site {i}", ["GTM-VENDOR"], null)));

        var networks = SiteNetworks.Find(signals);
        var owners = SiteNetworks.Owners(networks);

        Assert.Equal(2, networks.Count);
        Assert.DoesNotContain(networks, n => n.Id == "GTM-VENDOR");
        Assert.Equal(owners["Truth Daily"], owners["Free Voice"]);   // joined through Patriot Wire
        Assert.False(owners.ContainsKey("Honest Paper"));
    }

    [Fact]
    public void Independence_ShouldCountANetworkAsOneSource()
    {
        var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        WorldArticle R(string outlet, string title) => new(outlet, $"https://{outlet}.example/a", title, outlet, "English", "United States", at, "test");
        var articles = new[] { R("Truth Daily", "Shocking claim about the vote"), R("Patriot Wire", "Vote claim stuns officials"), R("Free Voice", "What they hide about the vote") };
        var before = SourceIndependence.NetworkOwners;
        try
        {
            SourceIndependence.NetworkOwners = SiteNetworks.Owners(SiteNetworks.Find(
            [
                new("truthdaily.example", "Truth Daily", ["GTM-AAAA1"], null),
                new("patriotwire.example", "Patriot Wire", ["GTM-AAAA1", "PUB-111"], null),
                new("freevoice.example", "Free Voice", ["PUB-111"], null),
            ]));

            Assert.Equal(1, SourceIndependence.Assess(articles).Independent);
        }
        finally
        {
            SourceIndependence.NetworkOwners = before;
        }
        Assert.Equal(3, SourceIndependence.Assess(articles).Independent);
    }

    [Fact]
    public void Registered_ShouldReadRdapsRegistrationEvent_AndNewSitesAreUnderSixMonths()
    {
        const string rdap = """{"events":[{"eventAction":"last changed","eventDate":"2026-10-01T00:00:00Z"},{"eventAction":"registration","eventDate":"2026-10-20T08:00:00Z"}]}""";
        var registered = SiteNetworks.Registered(rdap);
        var now = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), registered);
        Assert.True(SiteNetworks.IsNew(new SiteSignal("x.example", "X", [], registered), now));
        Assert.False(SiteNetworks.IsNew(new SiteSignal("y.example", "Y", [], now.AddYears(-3)), now));
        Assert.Null(SiteNetworks.Registered("{}"));
    }
}
