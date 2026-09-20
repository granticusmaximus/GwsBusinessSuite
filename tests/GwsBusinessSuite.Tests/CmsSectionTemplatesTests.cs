using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class CmsSectionTemplatesTests
{
    [Fact]
    public void All_ShouldOfferAtLeastAFewNonEmptyTemplatesWithUniqueKeys()
    {
        CmsSectionTemplates.All.Count.Should().BeGreaterThanOrEqualTo(5);
        CmsSectionTemplates.All.Select(template => template.Key).Should().OnlyHaveUniqueItems();
        CmsSectionTemplates.All.Should().OnlyContain(template =>
            template.Name.Length > 0 && template.Description.Length > 0 && template.Icon.Length > 0);
    }

    [Fact]
    public void Find_ShouldBeCaseInsensitiveAndReturnNullForAnUnknownKey()
    {
        CmsSectionTemplates.Find("FEATURE-GRID").Should().NotBeNull();
        CmsSectionTemplates.Find("not-a-real-template").Should().BeNull();
    }

    [Fact]
    public void EveryTemplate_ShouldBuildANonEmptySectionWithAtLeastOneWidget()
    {
        foreach (var template in CmsSectionTemplates.All)
        {
            var section = template.Build();

            section.Id.Should().NotBeNullOrWhiteSpace();
            section.Columns.Should().NotBeEmpty();
            section.Columns.SelectMany(c => c.Widgets).Should().NotBeEmpty();
        }
    }

    [Fact]
    public void EveryTemplate_ShouldGenerateFreshUniqueIdsPerBuildCall()
    {
        foreach (var template in CmsSectionTemplates.All)
        {
            var first = template.Build();
            var second = template.Build();

            first.Id.Should().NotBe(second.Id);

            var firstIds = AllIds(first);
            var secondIds = AllIds(second);
            firstIds.Should().OnlyHaveUniqueItems();
            secondIds.Should().OnlyHaveUniqueItems();
            firstIds.Should().NotIntersectWith(secondIds);
        }
    }

    private static List<string> AllIds(LayoutSection section)
    {
        var ids = new List<string> { section.Id };
        foreach (var column in section.Columns)
        {
            ids.Add(column.Id);
            ids.AddRange(column.Widgets.Select(w => w.Id));
        }
        return ids;
    }
}
