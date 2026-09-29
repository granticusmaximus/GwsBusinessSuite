using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class CmsThemePresetsTests
{
    [Fact]
    public void All_ShouldOfferAtLeastAFewPresetsWithUniqueKeys()
    {
        CmsThemePresets.All.Count.Should().BeGreaterThanOrEqualTo(3);
        CmsThemePresets.All.Select(preset => preset.Key).Should().OnlyHaveUniqueItems();
        CmsThemePresets.All.Should().OnlyContain(preset =>
            preset.Name.Length > 0 && preset.Description.Length > 0);
    }

    [Fact]
    public void All_ShouldOfferTheFull15CuratedThemes_WithDistinctNamesAndPalettes()
    {
        // Workstream D, Phase 2 - the master plan's own target count, spanning distinct niches
        // rather than 15 re-skins of the same palette.
        CmsThemePresets.All.Should().HaveCount(15);
        CmsThemePresets.All.Select(preset => preset.Name).Should().OnlyHaveUniqueItems();

        var primaryHexes = CmsThemePresets.All
            .Select(preset => preset.Tokens.Colors.First(c => c.Name == "Primary").Hex)
            .ToList();
        primaryHexes.Should().OnlyHaveUniqueItems("every theme should have its own distinct primary color, not share one with another preset");
    }

    [Fact]
    public void Find_ShouldBeCaseInsensitiveAndReturnNullForAnUnknownKey()
    {
        CmsThemePresets.Find("MINIMAL-NEUTRAL").Should().NotBeNull();
        CmsThemePresets.Find("not-a-real-theme").Should().BeNull();
    }

    [Fact]
    public void EveryPreset_ShouldHaveValidParseableDesignTokens()
    {
        foreach (var preset in CmsThemePresets.All)
        {
            var json = DesignTokenJson.Serialize(preset.Tokens);
            var roundTripped = DesignTokenJson.ParseOrEmpty(json);

            roundTripped.Colors.Should().NotBeEmpty();
            roundTripped.Colors.Should().OnlyContain(c => c.Name.Length > 0 && c.Hex.StartsWith('#'));
            roundTripped.TypeScale.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void EveryPreset_ShouldBuildANonEmptyHomepageLayout()
    {
        foreach (var preset in CmsThemePresets.All)
        {
            var layout = preset.HomepageLayout();

            layout.Sections.Should().NotBeEmpty();
            layout.Sections.SelectMany(s => s.Columns).SelectMany(c => c.Widgets).Should().NotBeEmpty();
            layout.Sections.SelectMany(s => s.Columns).SelectMany(c => c.Widgets)
                .Should().Contain(w => w.WidgetType == "hero");
        }
    }

    [Fact]
    public void EveryPreset_ShouldGenerateFreshUniqueIdsPerHomepageLayoutCall()
    {
        foreach (var preset in CmsThemePresets.All)
        {
            var first = preset.HomepageLayout();
            var second = preset.HomepageLayout();

            var firstIds = AllIds(first);
            var secondIds = AllIds(second);
            firstIds.Should().OnlyHaveUniqueItems();
            secondIds.Should().OnlyHaveUniqueItems();
            firstIds.Should().NotIntersectWith(secondIds);
        }
    }

    private static List<string> AllIds(PageLayout layout)
    {
        var ids = new List<string>();
        foreach (var section in layout.Sections)
        {
            ids.Add(section.Id);
            foreach (var column in section.Columns)
            {
                ids.Add(column.Id);
                ids.AddRange(column.Widgets.Select(w => w.Id));
            }
        }
        return ids;
    }
}
