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

    [Fact]
    public void NewsletterSignup_ShouldSeedAnEmailRoleFieldAndAutoCreateContact()
    {
        var section = CmsSectionTemplates.Find("newsletter-signup")!.Build();
        section.Background.Should().Be("accent");

        var form = section.Columns.SelectMany(c => c.Widgets).Single(w => w.WidgetType == "form");
        form.Props["autoCreateContact"].Should().Be("true");
        form.Props["submitLabel"].Should().Be("Subscribe");
        form.Props["fieldsJson"].Should().Contain("\"role\":\"email\"").And.Contain("\"type\":\"email\"");
    }

    [Fact]
    public void QuoteRequestForm_ShouldSeedContactAndProjectFields()
    {
        var section = CmsSectionTemplates.Find("quote-request-form")!.Build();
        var form = section.Columns.SelectMany(c => c.Widgets).Single(w => w.WidgetType == "form");

        form.Props["submitLabel"].Should().Be("Request Quote");
        form.Props["autoCreateContact"].Should().Be("true");
        form.Props["fieldsJson"].Should().Contain("\"role\":\"email\"").And.Contain("\"role\":\"name\"").And.Contain("\"role\":\"phone\"").And.Contain("\"role\":\"company\"");
    }

    [Fact]
    public void AppointmentRequestForm_ShouldSeedContactAndPreferredTimeFields()
    {
        var section = CmsSectionTemplates.Find("appointment-request-form")!.Build();
        var form = section.Columns.SelectMany(c => c.Widgets).Single(w => w.WidgetType == "form");

        form.Props["submitLabel"].Should().Be("Request Appointment");
        form.Props["autoCreateContact"].Should().Be("true");
        form.Props["fieldsJson"].Should().Contain("preferredDate").And.Contain("preferredTime").And.Contain("Morning");
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
