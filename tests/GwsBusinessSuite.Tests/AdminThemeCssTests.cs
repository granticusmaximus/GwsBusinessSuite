using System.Text.RegularExpressions;
using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class AdminThemeCssTests
{
    [Fact]
    public void NoTokens_KeepsTheBuiltInAdminColors()
    {
        AdminThemeCss.Build(null).Should().BeEmpty();
        AdminThemeCss.Build(DesignTokenSet.Empty).Should().BeEmpty();
        AdminThemeCss.Build(new DesignTokenSet([new DesignToken("Accent", "not-a-color")], [], [])).Should().BeEmpty();
    }

    [Fact]
    public void Theme_RecolorsAccentSidebarAndHeader_ButNotPageBackgrounds()
    {
        var css = AdminThemeCss.Build(Preset("bold-agency").Tokens);

        css.Should().Contain("--gws-sidebar-accent:#ff3d00;")
            .And.Contain("--bs-primary:#ff3d00;")
            .And.Contain("--bs-primary-rgb:255, 61, 0;")
            .And.Contain("--gws-sidebar-bg:#0a0a0a;")
            .And.Contain("--gws-admin-bar-bg:");
        // Page surfaces stay on the admin's own light/dark toggle.
        css.Should().NotContain("--gws-body-bg").And.NotContain("--bs-body-bg").And.NotContain("--gws-panel-bg");
    }

    [Theory]
    [MemberData(nameof(PresetKeys))]
    public void EveryPreset_KeepsAdminTextReadable(string presetKey)
    {
        var css = AdminThemeCss.Build(Preset(presetKey).Tokens);
        var shared = Block(css, ":root,:root[data-theme=\"light\"]");

        // Sidebar/top-bar text against the theme's primary color.
        AdminThemeCss.Contrast(Value(shared, "--gws-sidebar-text"), Value(shared, "--gws-sidebar-bg"))
            .Should().BeGreaterThanOrEqualTo(4.5);
        // Primary-button text against the accent.
        AdminThemeCss.Contrast(Value(shared, "--gws-accent-contrast"), Value(shared, "--gws-sidebar-accent"))
            .Should().BeGreaterThanOrEqualTo(3.0, "button labels are bold, large-text contrast applies");
        // Links against each admin canvas.
        AdminThemeCss.Contrast(Value(Block(css, ":root"), "--bs-link-color"), "#141210").Should().BeGreaterThanOrEqualTo(4.5);
        AdminThemeCss.Contrast(Value(Block(css, ":root[data-theme=\"light\"]"), "--bs-link-color"), "#ffffff").Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Fact]
    public void FindMatching_RecognizesAnAppliedPreset_AndTreatsEditsAsCustom()
    {
        var preset = Preset("warm-local-business");
        var applied = DesignTokenJson.Serialize(preset.Tokens);

        CmsThemePresets.FindMatching(applied)!.Key.Should().Be(preset.Key);
        CmsThemePresets.FindMatching(applied.Replace("#d97706", "#123456")).Should().BeNull();
        CmsThemePresets.FindMatching(null).Should().BeNull();
    }

    public static TheoryData<string> PresetKeys()
    {
        var data = new TheoryData<string>();
        foreach (var preset in CmsThemePresets.All) data.Add(preset.Key);
        return data;
    }

    private static CmsThemePreset Preset(string key) => CmsThemePresets.Find(key)!;

    private static string Block(string css, string selector)
    {
        // The shared selector ':root,:root[data-theme="light"]{' ends with the light selector's
        // own text, so look for the block that starts exactly at a selector boundary.
        var start = -1;
        for (var i = css.IndexOf(selector + "{", StringComparison.Ordinal); i >= 0; i = css.IndexOf(selector + "{", i + 1, StringComparison.Ordinal))
        {
            if (i == 0 || css[i - 1] == '}') { start = i; break; }
        }
        start.Should().BeGreaterThanOrEqualTo(0, $"the CSS should contain a '{selector}' block");
        var open = start + selector.Length + 1;
        return css[open..css.IndexOf('}', open)];
    }

    private static string Value(string block, string variable)
    {
        var match = Regex.Match(block, Regex.Escape(variable) + ":(#[0-9a-f]{6});");
        match.Success.Should().BeTrue($"{variable} should be set in '{block}'");
        return match.Groups[1].Value;
    }
}
