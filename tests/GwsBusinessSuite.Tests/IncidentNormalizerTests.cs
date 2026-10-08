using FluentAssertions;
using GwsBusinessSuite.Application.TrafficIncidents;

namespace GwsBusinessSuite.Tests;

public sealed class IncidentNormalizerTests
{
    private static TrafficIncident Incident(string eventType, string severity = "unknown", string description = "") =>
        new("id", "I-75", description, eventType, severity, 33, -84, "Test", "https://example.test");

    [Theory]
    [InlineData("accidentsAndIncidents", "crash")]       // GDOT
    [InlineData("crash", "crash")]                        // CARS511 style
    [InlineData("roadwork", "roadwork")]                  // GDOT
    [InlineData("work-zone", "roadwork")]                 // WZDx
    [InlineData("specialEvents", "event")]                // GDOT
    [InlineData("hazard", "hazard")]                      // CARS511
    [InlineData("closures", "closure")]                   // GDOT
    [InlineData("Warning", "other")]                      // NDDOT condition with no keyword
    public void Categorize_ShouldMapEachSourcesVocabulary(string eventType, string expected)
    {
        IncidentNormalizer.Categorize(Incident(eventType)).Should().Be(expected);
    }

    [Fact]
    public void Categorize_ShouldFallBackToTheDescription_AndPreferCrashOverClosure()
    {
        IncidentNormalizer.Categorize(Incident("unknown", description: "Crash on I-75 NB, road closed")).Should().Be(IncidentCategories.Crash);
        IncidentNormalizer.Categorize(Incident("unknown", description: "Debris in roadway")).Should().Be(IncidentCategories.Hazard);
        IncidentNormalizer.Categorize(Incident("unknown", description: "Lane closure for paving")).Should().Be(IncidentCategories.Roadwork);
    }

    [Theory]
    [InlineData("minor", "", "minor")]
    [InlineData("Major", "", "major")]
    [InlineData("some-lanes-closed", "", "moderate")]     // WZDx vehicle_impact
    [InlineData("all-lanes-open", "", "minor")]
    [InlineData("No Delays", "", "minor")]                // NDDOT
    [InlineData("unknown", "All lanes are closed", "major")]
    [InlineData("unknown", "", "unknown")]
    public void Severity_ShouldNormalize_AndNotGuessWhenNothingIsKnown(string severity, string description, string expected)
    {
        IncidentNormalizer.Normalize(Incident("crash", severity, description)).SeverityLevel.Should().Be(expected);
    }

    [Fact]
    public void Closures_WithNoSeverity_ShouldBeMajor()
    {
        IncidentNormalizer.Normalize(Incident("closures")).SeverityLevel.Should().Be(IncidentSeverityLevels.Major);
    }

    [Theory]
    [InlineData("Crash. Right lane blocked near exit 12.", "Right lane blocked")]
    [InlineData("2 of 3 lanes closed southbound", "2 of 3 lanes closed")]
    [InlineData("All lanes are closed until further notice", "All lanes are closed")]
    [InlineData("Vehicle on shoulder", null)]
    public void LanesFrom_ShouldPullTheLanePhraseOutOfTheDescription(string description, string? expected)
    {
        IncidentNormalizer.LanesFrom(description).Should().Be(expected);
    }

    [Fact]
    public void Normalize_ShouldKeepValuesAProviderAlreadySet()
    {
        var wzdx = Incident("detour") with { Category = IncidentCategories.Closure, Lanes = "All lanes closed" };

        var normalized = IncidentNormalizer.Normalize(wzdx);

        normalized.Category.Should().Be(IncidentCategories.Closure);
        normalized.Lanes.Should().Be("All lanes closed");
        normalized.SeverityLevel.Should().Be(IncidentSeverityLevels.Major);
        IncidentNormalizer.WzdxLanes("some-lanes-closed").Should().Be("Some lanes closed");
        IncidentNormalizer.WzdxLanes("unknown").Should().BeNull();
    }
}
