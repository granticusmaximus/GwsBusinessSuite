using System.Text.RegularExpressions;
using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// App-wide UX pass (12-11-26): the rules the review found drifting, kept from drifting again.
public sealed class DesignSystemGuardTests
{
    private static readonly string Web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web"));

    private static IEnumerable<string> Files(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Web, "Components"), pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Name(string path) => Path.GetRelativePath(Web, path);

    [Fact]
    public void PageStyles_ShouldUseTheAppsOneTypeface()
    {
        var serif = Files("*.razor.css")
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"Georgia|Playfair|--gws-font-serif"))
            .Select(Name);
        serif.Should().BeEmpty("page titles, numbers and headings use --gws-font-sans; serif is for user content only (equations, article previews)");

        var appCss = File.ReadAllText(Path.Combine(Web, "wwwroot/app.css"));
        Regex.Match(appCss, @"\.page-header-rich h1 \{[^}]*\}").Value.Should().Contain("var(--gws-font-sans)");
    }

    [Fact]
    public void PageStyles_ShouldSizeToTheContentArea_NotTheWindow()
    {
        // Shell parts outside .gws-content (the gws-main container) still need the viewport.
        string[] shell = ["AssistantPanel.razor.css", "NotificationBell.razor.css", "ReconnectModal.razor.css", "RecordInspector.razor.css"];
        var viewport = Files("*.razor.css")
            .Where(path => !shell.Contains(Path.GetFileName(path)))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"@media\s*\([^)]*(min|max)-width"))
            .Select(Name);
        viewport.Should().BeEmpty("admin pages use @container gws-main, so they fit beside the sidebar and in the Mac app");
    }

    // 19-11-26 (Apple-style item 7): visible text ends in a real ellipsis ("Saving…"), not three dots.
    // Overwatch's HUD and the boot screen keep their terminal "..." on purpose.
    [Fact]
    public void VisibleText_ShouldUseTheEllipsisCharacter()
    {
        string[] terminalStyle = ["OverwatchGrid.razor", "LoadingIndicator.razor", "BusyOverlay.razor"];
        var visibleDots = new Regex(@">[^<@]*[A-Za-z]\.\.\.[^<]*<|""[A-Z][^""]*[a-z]\.\.\.""");
        Files("*.razor")
            .Where(path => !terminalStyle.Contains(Path.GetFileName(path)))
            .SelectMany(path => File.ReadAllLines(path).Select((line, i) => (path, line, i)))
            .Where(x => !x.line.TrimStart().StartsWith("@*", StringComparison.Ordinal) && !x.line.Contains("*@", StringComparison.Ordinal)
                        && visibleDots.IsMatch(x.line))
            .Select(x => $"{Name(x.path)}:{x.i + 1}")
            .Should().BeEmpty();
    }

    [Fact]
    public void PanelsAndPages_ShouldLoadWithTheBootScreen_NotABareSpinner()
    {
        // 19-11-26 (Apple-style item 5): one activity indicator everywhere. Pages and panels that are
        // loading use LoadingIndicator; a button, status line or search box uses InlineProgress, which
        // takes the text's colour and pulses instead of spinning with Reduce Motion.
        Files("*.razor")
            .Where(path => File.ReadAllText(path).Contains("spinner-border", StringComparison.Ordinal)
                           || File.ReadAllText(path).Contains("spinner-grow", StringComparison.Ordinal))
            .Select(Name)
            .Should().BeEmpty("Bootstrap's spinners look and move differently from the app's own");
        var css = File.ReadAllText(Path.Combine(Web, "wwwroot/app.css"));
        Regex.IsMatch(css, @"prefers-reduced-motion: reduce\) \{\s*\.gws-progress \{[^}]*gws-progress-breathe")
            .Should().BeTrue("with Reduce Motion the indicator pulses instead of turning");
    }

    [Fact]
    public void GrowthAndIntelligencePages_ShouldFollowTheAppTheme()
    {
        var pages = Path.Combine(Web, "Components/Pages/BusinessSuite");
        var growth = File.ReadAllText(Path.Combine(pages, "Growth.razor.css"));
        growth.Should().Contain("--growth-ink: var(--gws-text-strong);").And.Contain("--growth-panel: var(--gws-panel-bg);");
        growth.Should().NotContain("--gws-text-strong: #", "Growth no longer pins the app's tokens to dark");
        foreach (var page in new[] { "ThreatIntelligence", "NewsIntelligence", "GovernmentIntelligence" })
        {
            var css = File.ReadAllText(Path.Combine(pages, page + ".razor.css"));
            Regex.Matches(css, @"--civic-accent(-strong)?:\s*([^;]+);").Select(match => match.Groups[2].Value.Trim())
                .Should().OnlyContain(value => value == "var(--gws-sidebar-accent)", $"{page} shares the app's one accent");
        }
    }

    [Fact]
    public void OverwatchText_ShouldNeverDropBelowTwelvePixels()
    {
        string[] sheets = ["wwwroot/tactical-globe.css", "Components/Pages/BusinessSuite/OverwatchGrid.razor.css"];
        foreach (var sheet in sheets)
        {
            var tiny = Regex.Matches(File.ReadAllText(Path.Combine(Web, sheet)), @"font-size:\s*(0?\.\d+)(rem|em)\b")
                .Where(match => double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 0.75)
                .Select(match => match.Value);
            tiny.Should().BeEmpty($"{sheet}: sizes under .75rem are written max(size, 12px)");
        }
    }

    [Fact]
    public void MacApp_ShouldOfferTheStandardCommands_AndAMinimumWindow()
    {
        var app = Path.GetFullPath(Path.Combine(Web, "../GwsBusinessSuite.App"));
        var menu = File.ReadAllText(Path.Combine(app, "Platforms/MacCatalyst/AppDelegate.cs"));
        menu.Should().Contain("\"gwsSettings:\"), \",\", UIKeyModifierFlags.Command")
            .And.Contain("\"gwsBack:\"), \"[\", UIKeyModifierFlags.Command")
            .And.Contain("\"gwsForward:\"), \"]\", UIKeyModifierFlags.Command")
            .And.Contain("\"gwsFind:\"), \"f\", UIKeyModifierFlags.Command")
            .And.Contain("builder.ReplaceMenu(UIMenuIdentifier.Find.GetConstant()!");
        File.ReadAllText(Path.Combine(app, "MainPage.xaml.cs")).Should().Contain("MacToolbarActions.EvaluateScript = script =>");
        File.ReadAllText(Path.Combine(app, "App.xaml.cs")).Should().Contain("window.MinimumWidth = 820;");
        File.ReadAllText(Path.Combine(Web, "wwwroot/app.js")).Should().Contain("window.addEventListener('gws-find'");
    }

    // Apple-style pass (18-11-26): the Mac toolbar's Sidebar and Search drive the page, and the page
    // drops its own copies only for an app build that has them.
    [Fact]
    public void MacApp_ToolbarSidebarAndSearch_ShouldReachThePage()
    {
        var mac = Path.GetFullPath(Path.Combine(Web, "../GwsBusinessSuite.App/Platforms/MacCatalyst"));
        File.ReadAllText(Path.Combine(mac, "MacWebViewCompatibility.cs")).Should().Contain("window.__gwsMacNativeChrome = true;");
        File.ReadAllText(Path.Combine(mac, "MacWindowToolbar.cs")).Should().Contain("\"toolbar-sidebar\"").And.Contain("\"toolbar-search\"");
        File.ReadAllText(Path.Combine(mac, "AppDelegate.cs")).Should()
            .Contain("new CustomEvent('gws-toggle-sidebar')").And.Contain("new CustomEvent('gws-open-search')");

        var appJs = File.ReadAllText(Path.Combine(Web, "wwwroot/app.js"));
        appJs.Should().Contain("window.addEventListener('gws-toggle-sidebar', toggleNavigation);")
            .And.Contain("window.addEventListener('gws-open-search', openCommand);");
        foreach (var script in new[] { appJs, File.ReadAllText(Path.Combine(Web, "wwwroot/js/theme-init.js")) })
            script.Should().Contain("window.__gwsMacNativeChrome === true ? 'native'").And.Contain("'data-gws-mac'");
        File.ReadAllText(Path.Combine(Web, "wwwroot/app.css")).Should()
            .NotContain("[data-gws-mac=\"native\"] .gws-admin-bar :is(.gws-sidebar-toggle", "the page's sidebar toggle must stay: the toolbar button is an extra, not a replacement");
    }

    private static IEnumerable<string> AdminSheets() =>
        Files("*.razor.css").Prepend(Path.Combine(Web, "wwwroot/app.css"));

    [Fact]
    public void AdminStyles_ShouldSizeTextFromTheTypeScale()
    {
        var literal = AdminSheets()
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"font-size\s*:\s*-?\d*\.?\d+(rem|px)\b").Select(match => $"{Name(path)}: {match.Value}"));
        literal.Should().BeEmpty("text sizes are var(--gws-type-*) tokens (app.css :root); clamp() and em stay allowed");
    }

    [Fact]
    public void AdminStyles_ShouldRoundCornersFromTheRadiusScale()
    {
        // Hairlines (up to 2px), percentages and calc() offsets for nested corners stay allowed.
        var literal = AdminSheets().SelectMany(path =>
            Regex.Matches(File.ReadAllText(path), @"border(-(top|bottom)-(left|right))?-radius\s*:\s*([^;}]+)")
                .Select(match => Regex.Replace(match.Groups[3].Value, @"(calc|var)\([^;}]*\)", ""))
                .Where(value => Regex.Matches(value, @"(?<![\w.-])(\d*\.?\d+)px\b")
                    .Select(px => double.Parse(px.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                    .Any(px => px > 2))
                .Select(value => $"{Name(path)}: {value.Trim()}"));
        literal.Should().BeEmpty("corners are var(--gws-radius-*) tokens (app.css :root)");
    }

    [Fact]
    public void AdminStyles_ShouldOnlyUseTokensThatExist()
    {
        // A token is defined by a stylesheet, set inline by a Razor component, or set by a script.
        var wwwroot = Path.Combine(Web, "wwwroot");
        var defined = Directory.EnumerateFiles(wwwroot, "*.css", SearchOption.AllDirectories)
            .Concat(Files("*.razor"))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"(--gws-[\w-]+)\s*:").Select(match => match.Groups[1].Value))
            .Concat(Directory.EnumerateFiles(wwwroot, "*.js", SearchOption.AllDirectories)
                .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"setProperty\(\s*'(--gws-[\w-]+)'").Select(match => match.Groups[1].Value)))
            .ToHashSet(StringComparer.Ordinal);

        var missing = AdminSheets().SelectMany(path =>
        {
            var css = File.ReadAllText(path);
            var unknown = Regex.Matches(css, @"var\(\s*(--gws-[\w-]+)\s*\)").Select(match => match.Groups[1].Value)
                .Where(token => !defined.Contains(token));
            // The admin pages don't load public-site.css, so a --neu-* token must be set in its own file.
            var neu = path.EndsWith(".razor.css", StringComparison.Ordinal)
                ? Regex.Matches(css, @"var\(\s*(--neu-[\w-]+)").Select(match => match.Groups[1].Value)
                    .Where(token => !Regex.IsMatch(css, Regex.Escape(token) + @"\s*:"))
                : Enumerable.Empty<string>();
            return unknown.Concat(neu).Distinct().Select(token => $"{Name(path)}: {token}");
        });
        missing.Should().BeEmpty("an undefined var() with no fallback silently drops the whole declaration");
    }

    [Fact]
    public void AdminStyles_ShouldHonourReduceMotion_AndUseTheMotionTokens()
    {
        var css = File.ReadAllText(Path.Combine(Web, "wwwroot/app.css"));
        css.Should().Contain("--gws-ease:").And.Contain("--gws-duration-fast:").And.Contain("--gws-duration-base:");
        Regex.IsMatch(css, @"prefers-reduced-motion: reduce\) \{\s*\*, \*::before, \*::after \{ transition-duration: 0s !important;")
            .Should().BeTrue("Reduce Motion turns transitions off app-wide");
    }

    [Fact]
    public void AdminStyles_ShouldNotGrowNewOneOffShadows()
    {
        // A ratchet (18-11-26, 185): lower it as shadows move onto --gws-neu-* and --gws-shadow-* tokens.
        const int ceiling = 185;
        var raw = AdminSheets().Sum(path => Regex.Matches(File.ReadAllText(path), @"box-shadow\s*:\s*([^;}]+)")
            .Count(match => !Regex.IsMatch(match.Groups[1].Value, @"^\s*(none|var\([^()]*\)|inherit|initial|unset)\s*(!important)?\s*$")));
        raw.Should().BeLessThanOrEqualTo(ceiling, "new shadows use the existing tokens");
    }

    // 19-11-26 (Grant): neumorphism married to Apple's flat style. Raised containers keep one soft
    // shadow (no double light-and-dark pair), contents inside a card are flat, the main button is
    // solid, focus is Apple blue and bars and menus are frosted glass, on the admin and public sites.
    [Fact]
    public void Styles_ShouldMarryNeumorphismWithTheFlatAppleStyle()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var admin = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/wwwroot/app.css"));
        var site = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/wwwroot/public-site.css"));

        admin.Should().NotContain("-5px -5px 12px var(--gws-neu-highlight)", "the double light-and-dark shadow is gone")
            .And.Contain("--gws-neu-raised-shadow: inset 0 1px 0 var(--gws-neu-highlight), 0 10px 26px -10px var(--gws-neu-shadow)")
            .And.Contain("/* ---- Soft containers, flat contents (19-11-26) ----")
            .And.Contain(".card .card,")
            .And.Contain(".btn.btn-primary:not(.btn-link) { box-shadow: none; }")
            .And.Contain("box-shadow: var(--gws-neu-focus-shadow);")
            .And.Contain("backdrop-filter: var(--gws-glass-filter);");
        site.Should().NotContain("-6px -6px 14px var(--neu-highlight)")
            .And.Contain("--neu-raised-shadow: inset 0 1px 0 var(--neu-highlight)");
    }
}

