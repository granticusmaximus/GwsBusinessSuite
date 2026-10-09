using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class WikiPersonValuesTests
{
    private static readonly string[] Users = ["grant", "Sam.Lee"];

    [Fact]
    public void Resolve_ShouldMatchStoredValuesToUsers_IgnoringCase()
    {
        var resolved = WikiPersonValues.Resolve(["Grant", "sam.lee", "Contractor Bob"], Users);

        resolved.Select(a => a.Username).Should().Equal("grant", "Sam.Lee", null);
        resolved[2].IsUser.Should().BeFalse();
        resolved[2].Value.Should().Be("Contractor Bob");
    }

    [Fact]
    public void Toggle_On_ShouldStoreTheExactUsername()
    {
        WikiPersonValues.Toggle([], "Sam.Lee", assign: true).Should().Equal("Sam.Lee");
    }

    [Fact]
    public void Toggle_Off_ShouldAlsoClearAnOldFreeTextSpellingOfTheSameUser()
    {
        WikiPersonValues.Toggle(["Grant", "Contractor Bob"], "grant", assign: false).Should().Equal("Contractor Bob");
    }

    [Fact]
    public void Toggle_On_ShouldNotDuplicateAnExistingFreeTextSpelling()
    {
        WikiPersonValues.Toggle(["GRANT"], "grant", assign: true).Should().Equal("grant");
    }

    [Fact]
    public void IsAssigned_ShouldMatchTheWayMyWorkDoes()
    {
        WikiPersonValues.IsAssigned([" Grant "], "grant").Should().BeTrue();
        WikiPersonValues.IsAssigned(["grantw"], "grant").Should().BeFalse();
    }

    [Fact]
    public void Remove_ShouldDropOnlyThatValue()
    {
        WikiPersonValues.Remove(["Contractor Bob", "grant"], "Contractor Bob").Should().Equal("grant");
    }
}
