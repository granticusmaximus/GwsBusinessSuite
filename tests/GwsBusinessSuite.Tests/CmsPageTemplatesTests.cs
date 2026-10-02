using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class CmsPageTemplatesTests
{
    public static TheoryData<string> TemplateKeys()
    {
        var data = new TheoryData<string>();
        foreach (var template in CmsPageTemplates.All) data.Add(template.Key);
        return data;
    }

    [Fact]
    public void Catalog_HasUniqueKeys_AndCompleteCopy()
    {
        CmsPageTemplates.All.Should().HaveCountGreaterThanOrEqualTo(10);
        CmsPageTemplates.All.Select(t => t.Key).Should().OnlyHaveUniqueItems();
        CmsPageTemplates.All.Should().AllSatisfy(t =>
        {
            t.Name.Should().NotBeNullOrWhiteSpace();
            t.Description.Should().NotBeNullOrWhiteSpace();
            t.DefaultTitle.Should().NotBeNullOrWhiteSpace();
            t.Category.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public void Template_RoundTripsThroughSavedJson_AndRenders(string key)
    {
        var template = CmsPageTemplates.Find(key)!;
        var layout = template.Build();

        layout.Sections.Should().HaveCountGreaterThanOrEqualTo(2, "a pre-built page should be composed, not a single block");
        layout.Sections.SelectMany(s => s.Columns).SelectMany(c => c.Widgets).Should().NotBeEmpty();

        // What the builder stores is what it must be able to load back.
        var reloaded = CmsBuilderJson.ParseLayout(CmsBuilderJson.Serialize(layout));
        reloaded.Should().NotBeNull();
        reloaded!.Sections.Should().HaveCount(layout.Sections.Count);

        var html = CmsBlockHtmlRenderer.Render(reloaded, "site", key, articles: []);
        html.Should().Contain("<section");
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public void Template_UsesThemeAwareBackgroundsOnly(string key)
    {
        // Templates must wear the site's theme, so no section may carry a background outside the
        // renderer's theme-driven variants.
        CmsPageTemplates.Find(key)!.Build().Sections
            .Select(s => s.Background)
            .Should().OnlyContain(b => b == "transparent" || b == "light" || b == "dark" || b == "accent");
    }

    [Fact]
    public void EachBuild_GetsFreshIds_SoTwoPagesFromOneTemplateNeverShareSectionIds()
    {
        var template = CmsPageTemplates.Find("about")!;
        var first = template.Build().Sections.Select(s => s.Id);
        var second = template.Build().Sections.Select(s => s.Id);

        first.Should().NotIntersectWith(second);
    }
}
