using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Tests;

// Property-level validation (Required / Number min-max / a regex pattern for text-shaped
// properties) - pure logic, no database, mirroring WikiDatabaseViewLogicTests's own split for
// the same reason (WikiDatabasePropertyValidation is DB-free by design).
public sealed class WikiDatabasePropertyValidationTests
{
    [Fact]
    public void Validate_ShouldReportARequiredPropertyLeftBlank()
    {
        var status = RequiredProperty(WikiDatabasePropertyTypes.Text);
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();

        var errors = WikiDatabasePropertyValidation.Validate([status], values);

        errors.Should().ContainSingle().Which.Should().Be($"{status.Name} is required.");
    }

    [Fact]
    public void Validate_ShouldAcceptARequiredPropertyThatHasAValue()
    {
        var status = RequiredProperty(WikiDatabasePropertyTypes.Text);
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetText(values, status.Id, "In progress");

        WikiDatabasePropertyValidation.Validate([status], values).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldTreatAFalseCheckboxAsPresent_NotBlank()
    {
        // A checkbox left unchecked is a real, deliberate "no" - not an unanswered question,
        // unlike an empty text field or an unset select. Required must not reject it.
        var agreed = RequiredProperty(WikiDatabasePropertyTypes.Checkbox);
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetCheckbox(values, agreed.Id, false);

        WikiDatabasePropertyValidation.Validate([agreed], values).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldTreatAnEmptyMultiSelectArrayAsBlank()
    {
        var tags = RequiredProperty(WikiDatabasePropertyTypes.MultiSelect);
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetMultiSelect(values, tags.Id, []);

        WikiDatabasePropertyValidation.Validate([tags], values)
            .Should().ContainSingle().Which.Should().Be($"{tags.Name} is required.");
    }

    [Theory]
    [InlineData(WikiDatabasePropertyTypes.Title)]
    [InlineData(WikiDatabasePropertyTypes.Formula)]
    [InlineData(WikiDatabasePropertyTypes.UniqueId)]
    public void Validate_ShouldNeverRequireAComputedOrTitleProperty_EvenIfConfiguredTo(string type)
    {
        // Title's requiredness is enforced by the schema itself (exactly one per database);
        // computed types have no user-writable value at all - a Required flag on either would
        // be a config mistake, not something a person filling out the row could ever satisfy.
        var property = RequiredProperty(type);
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();

        WikiDatabasePropertyValidation.Validate([property], values).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldRejectANumberBelowItsConfiguredMinimum()
    {
        var budget = NewProperty(WikiDatabasePropertyTypes.Number);
        budget.ConfigJson = WikiDatabasePropertyConfig.Serialize(
            new WikiDatabasePropertyConfiguration([], null, null, null, null, null, null, MinValue: 100));
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetNumber(values, budget.Id, 40m);

        WikiDatabasePropertyValidation.Validate([budget], values)
            .Should().ContainSingle().Which.Should().Be($"{budget.Name} must be at least 100.");
    }

    [Fact]
    public void Validate_ShouldRejectANumberAboveItsConfiguredMaximum()
    {
        var budget = NewProperty(WikiDatabasePropertyTypes.Number);
        budget.ConfigJson = WikiDatabasePropertyConfig.Serialize(
            new WikiDatabasePropertyConfiguration([], null, null, null, null, null, null, MaxValue: 1000));
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetNumber(values, budget.Id, 5000m);

        WikiDatabasePropertyValidation.Validate([budget], values)
            .Should().ContainSingle().Which.Should().Be($"{budget.Name} must be at most 1000.");
    }

    [Fact]
    public void Validate_ShouldAcceptANumberWithinItsConfiguredRange()
    {
        var budget = NewProperty(WikiDatabasePropertyTypes.Number);
        budget.ConfigJson = WikiDatabasePropertyConfig.Serialize(
            new WikiDatabasePropertyConfiguration([], null, null, null, null, null, null, MinValue: 100, MaxValue: 1000));
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetNumber(values, budget.Id, 500m);

        WikiDatabasePropertyValidation.Validate([budget], values).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldRejectTextThatDoesNotMatchItsConfiguredPattern()
    {
        var sku = NewProperty(WikiDatabasePropertyTypes.Text);
        sku.ConfigJson = WikiDatabasePropertyConfig.Serialize(
            new WikiDatabasePropertyConfiguration([], null, null, null, null, null, null, ValidationPattern: @"^SKU-\d{4}$"));
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetText(values, sku.Id, "not-a-sku");

        WikiDatabasePropertyValidation.Validate([sku], values)
            .Should().ContainSingle().Which.Should().Be($"{sku.Name} doesn't match the required format.");
    }

    [Fact]
    public void Validate_ShouldAcceptTextThatMatchesItsConfiguredPattern()
    {
        var sku = NewProperty(WikiDatabasePropertyTypes.Text);
        sku.ConfigJson = WikiDatabasePropertyConfig.Serialize(
            new WikiDatabasePropertyConfiguration([], null, null, null, null, null, null, ValidationPattern: @"^SKU-\d{4}$"));
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetText(values, sku.Id, "SKU-1234");

        WikiDatabasePropertyValidation.Validate([sku], values).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldTreatAnInvalidPatternAsNoPattern_RatherThanRejectingEveryValue()
    {
        // An unbalanced/invalid regex must not lock the property for everyone - a config typo
        // should degrade to "no validation", not "nothing can ever be saved".
        var sku = NewProperty(WikiDatabasePropertyTypes.Text);
        sku.ConfigJson = WikiDatabasePropertyConfig.Serialize(
            new WikiDatabasePropertyConfiguration([], null, null, null, null, null, null, ValidationPattern: "[unbalanced("));
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetText(values, sku.Id, "anything at all");

        WikiDatabasePropertyValidation.Validate([sku], values).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldIgnoreAPatternConfiguredOnANonTextShapedProperty()
    {
        // ValidationPattern is documented as text-shaped-only (Text/Url/Email/Phone) - a
        // pattern saved against, say, a Number property (e.g. from an old config, or a UI bug)
        // must not silently start rejecting numbers.
        var quantity = NewProperty(WikiDatabasePropertyTypes.Number);
        quantity.ConfigJson = WikiDatabasePropertyConfig.Serialize(
            new WikiDatabasePropertyConfiguration([], null, null, null, null, null, null, ValidationPattern: "^[a-z]+$"));
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetNumber(values, quantity.Id, 42m);

        WikiDatabasePropertyValidation.Validate([quantity], values).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldReportEveryFailingPropertyInOneCall()
    {
        var name = RequiredProperty(WikiDatabasePropertyTypes.Text, "Full name");
        var budget = NewProperty(WikiDatabasePropertyTypes.Number, "Budget");
        budget.ConfigJson = WikiDatabasePropertyConfig.Serialize(
            new WikiDatabasePropertyConfiguration([], null, null, null, null, null, null, MinValue: 100));
        var values = System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject();
        WikiPropertyValues.SetNumber(values, budget.Id, 10m);

        var errors = WikiDatabasePropertyValidation.Validate([name, budget], values);

        errors.Should().BeEquivalentTo(["Full name is required.", "Budget must be at least 100."]);
    }

    private static WikiDatabaseProperty NewProperty(string type, string name = "Property") => new()
    {
        Id = Guid.NewGuid(),
        WikiDatabaseId = Guid.NewGuid(),
        Name = name,
        Type = type
    };

    private static WikiDatabaseProperty RequiredProperty(string type, string name = "Property")
    {
        var property = NewProperty(type, name);
        property.ConfigJson = WikiDatabasePropertyConfig.Serialize(
            new WikiDatabasePropertyConfiguration([], null, null, null, null, null, null, IsRequired: true));
        return property;
    }
}
