using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Tests;

// Nav hiding for features that have never held a row in production. The subtle part is null vs
// empty: null means "never configured" and takes the defaults, empty means "show me everything".
// Collapsing the two would make the setting impossible to clear - every save would silently
// re-hide what the user just revealed.
public sealed class AdminFeaturesTests
{
    [Fact]
    public void Parse_ShouldApplyDefaults_WhenNeverConfigured()
    {
        AdminFeatures.Parse(null).Should().BeEquivalentTo(AdminFeatures.DefaultHiddenKeys);
        AdminFeatures.Parse(null).Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_ShouldShowEverything_WhenExplicitlyCleared()
    {
        // The user unticked everything. That must survive a round trip, not revert to defaults.
        AdminFeatures.Parse(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void SerializeThenParse_ShouldRoundTrip()
    {
        var chosen = new[] { "support", "billing" };
        AdminFeatures.Parse(AdminFeatures.Serialize(chosen)).Should().BeEquivalentTo(chosen);
    }

    [Fact]
    public void SerializeThenParse_ShouldRoundTripAnEmptySelection()
    {
        // The regression that would make "show everything" impossible.
        AdminFeatures.Parse(AdminFeatures.Serialize([])).Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldToleratePaddingAndBlanks()
    {
        AdminFeatures.Parse(" support , ,billing ").Should().BeEquivalentTo(["support", "billing"]);
    }

    [Fact]
    public void IsHidden_ShouldIgnoreCase()
    {
        AdminFeatures.IsHidden("Support,billing", "SUPPORT").Should().BeTrue();
        AdminFeatures.IsHidden("support", "crm").Should().BeFalse();
    }

    [Fact]
    public void EveryDefaultHiddenKey_ShouldBeARealFeature()
    {
        // A typo in the defaults would hide nothing and be invisible - the nav would simply not
        // match, with no error anywhere.
        AdminFeatures.DefaultHiddenKeys.Should().OnlyContain(
            key => AdminFeatures.All.Any(feature => feature.Key == key));
    }

    [Fact]
    public void TheCatalogue_ShouldExcludeAreasThatAreInUse()
    {
        // These have real production data, or are empty precisely because things are healthy
        // (no security incidents, no privacy requests). Offering to hide them would be wrong.
        var keys = AdminFeatures.All.Select(feature => feature.Key).ToList();
        keys.Should().NotContain(["sentinel", "automation", "pages", "media", "security-audit",
                                  "privacy-operations", "affiliate-analytics", "content-studio"]);
    }

    [Fact]
    public void EveryFeature_ShouldExplainWhyItIsACandidate()
    {
        // The reason is shown next to each checkbox: a list of togglable features with no
        // justification is just a switchboard.
        AdminFeatures.All.Should().OnlyContain(feature =>
            !string.IsNullOrWhiteSpace(feature.Reason) && !string.IsNullOrWhiteSpace(feature.DisplayName));
    }
}
