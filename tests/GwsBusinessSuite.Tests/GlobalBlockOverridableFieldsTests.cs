using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;

namespace GwsBusinessSuite.Tests;

public sealed class GlobalBlockOverridableFieldsTests
{
    [Fact]
    public void SerializeThenParse_ShouldRoundTrip_AndDeduplicate()
    {
        var json = GlobalBlockOverridableFields.Serialize(["title", "link", "title", "  ", ""]);

        var parsed = GlobalBlockOverridableFields.Parse(json);

        parsed.Should().BeEquivalentTo(["title", "link"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"not\":\"an array\"}")]
    public void Parse_ShouldReturnEmptyList_ForBlankOrMalformedInput(string? json)
    {
        GlobalBlockOverridableFields.Parse(json).Should().BeEmpty();
    }

    [Fact]
    public void CandidatesFor_ShouldReturnTheCuratedContentFields_ForAKnownWidgetType()
    {
        GlobalBlockOverridableFields.CandidatesFor("card").Should().BeEquivalentTo(["title", "body", "link", "imageSrc"]);
    }

    [Fact]
    public void CandidatesFor_ShouldReturnEmpty_ForAnUnknownWidgetType()
    {
        GlobalBlockOverridableFields.CandidatesFor("spacer").Should().BeEmpty();
    }
}
