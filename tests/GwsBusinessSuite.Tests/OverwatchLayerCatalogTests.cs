using FluentAssertions;
using GwsBusinessSuite.Web;

namespace GwsBusinessSuite.Tests;

// Overwatch layer health (13-11-26): every failure name the page reports maps onto a catalogue layer.
public sealed class OverwatchLayerCatalogTests
{
    [Fact]
    public void EveryReportedLayerName_ShouldBeInTheCatalogue()
    {
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "GwsBusinessSuite.Web", "Components", "Pages", "BusinessSuite", "OverwatchGrid.razor"));
        var reported = System.Text.RegularExpressions.Regex.Matches(page, "(?:NoteLayerProblem|TimedAsync)\\(\"([^\"]+)\"")
            .Select(match => OverwatchLayerCatalog.Canonical(match.Groups[1].Value)).Distinct().ToList();
        var catalogue = OverwatchLayerCatalog.Layers.Select(layer => layer.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        reported.Should().NotBeEmpty();
        reported.Should().OnlyContain(name => catalogue.Contains(name), "a problem must show against a listed layer");
    }

    [Fact]
    public void Record_ShouldKeepCountsSlowestAndTheLastGoodLoad()
    {
        var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var first = OverwatchLayerCatalog.Record(null, at, TimeSpan.FromSeconds(2), failed: false);
        var second = OverwatchLayerCatalog.Record(first, at.AddMinutes(1), TimeSpan.FromSeconds(1), failed: true);

        Assert.Equal(2, second.Count);
        Assert.Equal(1, second.Failed);
        Assert.Equal(TimeSpan.FromSeconds(2), second.Slowest);
        Assert.Equal(TimeSpan.FromSeconds(1), second.LastTook);
        Assert.Equal(at, second.LastGoodAt);
        Assert.Contains("1 FAILED", OverwatchLayerCatalog.Describe(second));
        Assert.Contains("NO GOOD LOAD YET", OverwatchLayerCatalog.Describe(OverwatchLayerCatalog.Record(null, at, TimeSpan.Zero, failed: true)));
    }
}
