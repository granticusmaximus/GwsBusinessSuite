using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class CmsThemeCssTests
{
    [Fact]
    public void EmptyTokens_PreserveExistingStyling()
    {
        CmsThemeCss.Build(null).Should().BeEmpty();
        CmsThemeCss.Build(DesignTokenSet.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Presets_ProduceDistinctSiteStyles_WithoutChangingTokens()
    {
        var styles = new List<string>();
        foreach (var preset in CmsThemePresets.All)
        {
            var before = DesignTokenJson.Serialize(preset.Tokens);
            var css = CmsThemeCss.Build(preset.Tokens);
            css.Should().Contain("--accent:" + preset.Tokens.Colors.First(c => c.Name == "Accent").Hex);
            css.Should().Contain("--bg:" + preset.Tokens.Colors.First(c => c.Name == "Surface").Hex);
            DesignTokenJson.Serialize(preset.Tokens).Should().Be(before);
            styles.Add(css);
        }
        styles.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void InvalidValues_CannotEscapeStyleElement()
    {
        var tokens = new DesignTokenSet(
            [new("Accent", "</style><script>alert(1)</script>"), new("Text", "red;}")],
            [new("Body", "1rem;}body{display:none")], []);
        CmsThemeCss.Build(tokens).Should().BeEmpty();
    }
}
