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
}
