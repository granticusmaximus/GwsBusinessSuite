using FluentAssertions;
using GwsBusinessSuite.Application.GovernmentIntelligence;

namespace GwsBusinessSuite.Tests;

// Backs both the civic.extractEvents workflow node and the Eventbrite source. These cover the
// shapes real sites actually publish - the awkward ones are why this walks the document instead
// of following a fixed path, and why the state-blob scan is brace-matched rather than regexed.
public sealed class SchemaOrgEventExtractorTests
{
    [Fact]
    public void Extract_ShouldReadStandardLdJson()
    {
        const string html = """
            <html><head>
            <script type="application/ld+json">
            {"@context":"https://schema.org","@type":"Event","name":"Mossy Creek Festival",
             "url":"https://example.org/mossy","startDate":"2026-10-18T10:00:00-04:00",
             "location":{"@type":"Place","name":"Georgia National Fairgrounds",
               "address":{"@type":"PostalAddress","addressLocality":"Perry","addressRegion":"GA"}}}
            </script></head><body></body></html>
            """;

        var events = SchemaOrgEventExtractor.Extract(html);

        events.Should().ContainSingle();
        events[0].Title.Should().Be("Mossy Creek Festival");
        events[0].City.Should().Be("Perry");
        events[0].Venue.Should().Be("Georgia National Fairgrounds");
        events[0].StartAt.Should().NotBeNull();
    }

    [Fact]
    public void Extract_ShouldReadEventsFromAReactStateBlob()
    {
        // Eventbrite's browse pages carry their listings here rather than in ld+json, and more
        // script follows in the same tag - a lazy regex to "};" would capture the wrong span.
        const string html = """
            <script>window.__SERVER_DATA__ = {"jsonld":[{"@type":"Event","name":"Trivia Night",
              "url":"https://example.org/trivia","startDate":"2026-09-20",
              "location":{"@type":"Place","name":"The Patio",
                "address":{"addressLocality":"Warner Robins"},
                "geo":{"latitude":"32.5885","longitude":"-83.6199"}}}]};
            window.__REACT_QUERY_STATE__ = {"queries":[]}</script>
            """;

        var events = SchemaOrgEventExtractor.Extract(html);

        events.Should().ContainSingle();
        events[0].City.Should().Be("Warner Robins");
        events[0].Latitude.Should().BeApproximately(32.5885, 0.0001);
        events[0].MilesFromHome.Should().NotBeNull("coordinates give a real per-venue distance");
        events[0].MilesFromHome.Should().BeInRange(5, 15, "Warner Robins is a short drive from Kathleen");
    }

    [Fact]
    public void Extract_ShouldFlagOnlineEvents_SoCallersCanDropThem()
    {
        // Searching a small town on Eventbrite returns mostly webinars, which are listed under
        // the town but happen nowhere near it.
        const string html = """
            <script type="application/ld+json">
            {"@type":"Event","name":"Webinar: Parkinson's and dementia","url":"https://example.org/w",
             "eventAttendanceMode":"https://schema.org/OnlineEventAttendanceMode",
             "location":{"@type":"VirtualLocation","url":"https://zoom.example"}}
            </script>
            """;

        SchemaOrgEventExtractor.Extract(html).Single().IsOnline.Should().BeTrue();
    }

    [Fact]
    public void Extract_ShouldAcceptEventSubtypesAndTypeArrays()
    {
        // Real sites publish MusicEvent/Festival, and some give @type as an array.
        const string html = """
            <script type="application/ld+json">
            [{"@type":"MusicEvent","name":"Concert on the Green","url":"https://example.org/a"},
             {"@type":["Event","SocialEvent"],"name":"Downtown Market","url":"https://example.org/b"}]
            </script>
            """;

        SchemaOrgEventExtractor.Extract(html).Select(e => e.Title)
            .Should().BeEquivalentTo(["Concert on the Green", "Downtown Market"]);
    }

    [Fact]
    public void Extract_ShouldDeduplicate_WhenAPageListsAnEventTwice()
    {
        // Pages routinely carry an event once in an ItemList and again as a standalone block.
        const string html = """
            <script type="application/ld+json">
            {"@type":"ItemList","itemListElement":[
              {"@type":"ListItem","item":{"@type":"Event","name":"Cherry Blossom","url":"https://example.org/cb"}}]}
            </script>
            <script type="application/ld+json">
            {"@type":"Event","name":"Cherry Blossom","url":"https://example.org/cb"}
            </script>
            """;

        SchemaOrgEventExtractor.Extract(html).Should().ContainSingle();
    }

    [Fact]
    public void Extract_ShouldHandleBracesAndQuotesInsideDescriptions()
    {
        const string html = """
            <script>window.__SERVER_DATA__ = {"e":{"@type":"Event","name":"Art {Show}",
              "description":"He said \"come\" - {details} inside","url":"https://example.org/art"}};</script>
            """;

        var events = SchemaOrgEventExtractor.Extract(html);

        events.Should().ContainSingle();
        events[0].Title.Should().Be("Art {Show}");
    }

    [Fact]
    public void Extract_ShouldReturnEmpty_ForPagesWithNoStructuredData()
    {
        SchemaOrgEventExtractor.Extract("<html><body><h1>Events</h1></body></html>").Should().BeEmpty();
        SchemaOrgEventExtractor.Extract(null).Should().BeEmpty();
    }

    [Fact]
    public void Extract_ShouldSurviveOneMalformedBlob()
    {
        // A page can carry several blobs; one bad one must not lose the others.
        const string html = """
            <script type="application/ld+json">{ this is not json </script>
            <script type="application/ld+json">{"@type":"Event","name":"Still Found","url":"https://example.org/ok"}</script>
            """;

        SchemaOrgEventExtractor.Extract(html).Single().Title.Should().Be("Still Found");
    }
}
