using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class DesignTokenModelsTests
{
    [Fact]
    public void SerializeThenParseOrEmpty_ShouldRoundTripAllThreeScales()
    {
        var tokens = new DesignTokenSet(
            [new DesignToken("Primary", "#1c3d5a"), new DesignToken("Accent", "#d96a2b")],
            [new TypeScaleStep("Body", "1rem"), new TypeScaleStep("Heading", "2rem")],
            [new SpacingScaleStep("Gutter", "1.5rem")]);

        var json = DesignTokenJson.Serialize(tokens);
        var parsed = DesignTokenJson.ParseOrEmpty(json);

        parsed.Colors.Should().HaveCount(2);
        parsed.Colors.Should().Contain(color => color.Name == "Primary" && color.Hex == "#1c3d5a");
        parsed.TypeScale.Should().Contain(step => step.Name == "Heading" && step.RemValue == "2rem");
        parsed.SpacingScale.Should().ContainSingle(step => step.Name == "Gutter" && step.RemValue == "1.5rem");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseOrEmpty_ShouldReturnEmptySet_ForBlankInput(string? json)
    {
        var result = DesignTokenJson.ParseOrEmpty(json);

        result.Colors.Should().BeEmpty();
        result.TypeScale.Should().BeEmpty();
        result.SpacingScale.Should().BeEmpty();
    }

    [Fact]
    public void ParseOrEmpty_ShouldReturnEmptyLists_NotNull_ForABareEmptyObject()
    {
        // "{}" deserializes to DesignTokenSet(null, null, null) via System.Text.Json - it
        // doesn't require every record constructor parameter to be present in the JSON. Callers
        // must always get real (possibly empty) lists back, never null.
        var result = DesignTokenJson.ParseOrEmpty("{}");

        result.Colors.Should().NotBeNull().And.BeEmpty();
        result.TypeScale.Should().NotBeNull().And.BeEmpty();
        result.SpacingScale.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void ParseOrEmpty_ShouldDegradeToEmptySet_ForMalformedJson_RatherThanThrowing()
    {
        var result = DesignTokenJson.ParseOrEmpty("[1,2,3]");

        result.Should().Be(DesignTokenSet.Empty);
    }
}
