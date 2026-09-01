using FluentAssertions;
using GwsBusinessSuite.Web.Services;

namespace GwsBusinessSuite.Tests;

public class RelationshipChipFormattingTests
{
    [Fact]
    public void ContactDetailUrl_ShouldPointAtTheCrmContactDetailRoute()
    {
        var contactId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        RelationshipChipFormatting.ContactDetailUrl(contactId)
            .Should().Be("/admin/crm/contacts/11111111-2222-3333-4444-555555555555");
    }

    [Theory]
    [InlineData("Grant Watson", "G")]
    [InlineData("  grant watson", "G")]
    [InlineData("élan", "É")]
    public void Initial_ShouldUppercaseTheFirstNonWhitespaceCharacter(string contactName, string expected)
    {
        RelationshipChipFormatting.Initial(contactName).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Initial_ShouldFallBackToQuestionMark_WhenNameIsMissing(string? contactName)
    {
        RelationshipChipFormatting.Initial(contactName).Should().Be("?");
    }
}
