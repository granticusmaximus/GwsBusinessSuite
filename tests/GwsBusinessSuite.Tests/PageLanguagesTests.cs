using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Localization;

namespace GwsBusinessSuite.Tests;

// hreflang links, <html lang> and the language switcher for pages with published translations.
public sealed class PageLanguagesTests
{
    private const string Url = "https://grantwatson.dev/portfolio";

    [Fact]
    public void Create_ShouldBeNull_WithoutTranslations()
    {
        PageLanguages.Create(Url, "/portfolio", [], "es").Should().BeNull();
        PageLanguages.Create(Url, "/portfolio", ["en", " "], null).Should().BeNull("the original language alone isn't a choice");
    }

    [Fact]
    public void Create_ShouldOnlyHonourPublishedLanguages()
    {
        var spanish = PageLanguages.Create(Url, "/portfolio", ["FR", "es"], "ES")!;
        spanish.Current.Should().Be("es");
        spanish.IsTranslated.Should().BeTrue();
        spanish.Codes.Should().Equal("en", "es", "fr");

        PageLanguages.Create(Url, "/portfolio", ["es"], "de")!.Current.Should().Be("en", "German isn't published");
        PageLanguages.Create(Url, "/portfolio", ["es"], null)!.IsTranslated.Should().BeFalse();
    }

    [Fact]
    public void HeadLinks_ShouldPairEveryLanguage_WithXDefaultOnTheOriginal()
    {
        PageLanguages.Create(Url, "/portfolio", ["es"], null)!.HeadLinks().Should().Be(
            "<link rel=\"alternate\" hreflang=\"en\" href=\"https://grantwatson.dev/portfolio\" />"
            + "<link rel=\"alternate\" hreflang=\"es\" href=\"https://grantwatson.dev/portfolio?lang=es\" />"
            + "<link rel=\"alternate\" hreflang=\"x-default\" href=\"https://grantwatson.dev/portfolio\" />");
    }

    [Fact]
    public void Switcher_ShouldNameEachLanguageInItself_AndMarkTheCurrentOne()
    {
        var html = PageLanguages.Create(Url, "/portfolio", ["es", "xx"], "es")!.Switcher();

        html.Should().Contain("<a href=\"/portfolio\" hreflang=\"en\" lang=\"en\">English</a>");
        html.Should().Contain("<a href=\"/portfolio?lang=es\" hreflang=\"es\" lang=\"es\" aria-current=\"true\">Español</a>");
        html.Should().Contain(">XX</a>", "an unlisted code falls back to the code itself");
    }

    [Fact]
    public void Layout_ShouldCarryTheLanguageIntoThePage()
    {
        var languages = PageLanguages.Create(Url, "/portfolio", ["es"], "es");
        var html = PublicSiteHtmlRenderer.Layout("Portafolio", "D", null, "<p>Hola</p>", canonicalUrl: languages!.AbsoluteUrlFor("es"), languages: languages);

        html.Should().Contain("<html lang=\"es\">");
        html.Should().Contain("<link rel=\"canonical\" href=\"https://grantwatson.dev/portfolio?lang=es\" />");
        html.Should().Contain("hreflang=\"x-default\"");
        html.IndexOf("gws-lang-switcher", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("site-footer", StringComparison.Ordinal), "above the footer");

        PublicSiteHtmlRenderer.Layout("T", "D", null, "<p>x</p>").Should().Contain("<html lang=\"en\">").And.NotContain("hreflang");
    }
}
