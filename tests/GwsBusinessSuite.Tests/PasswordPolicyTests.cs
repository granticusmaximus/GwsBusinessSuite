using FluentAssertions;
using GwsBusinessSuite.Application.Users;

namespace GwsBusinessSuite.Tests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("short7x!")]
    [InlineData("eleven11ch")]
    public void IsWeak_ForAPasswordShorterThanTheMinimumLength_ShouldBeWeak(string password)
    {
        PasswordPolicy.IsWeak(password, "someone", out var reason).Should().BeTrue();
        reason.Should().Contain($"at least {PasswordPolicy.MinLength} characters");
    }

    [Fact]
    public void IsWeak_WhenThePasswordMatchesTheUsername_ShouldBeWeakRegardlessOfCase()
    {
        PasswordPolicy.IsWeak("GrantWatson12", "grantwatson12", out var reason).Should().BeTrue();
        reason.Should().Contain("username");
    }

    // Every other entry in the blocklist ("password", "admin", "changeme", "letmein",
    // "12345678", "123456789", "qwertyuiop", "password123") is shorter than MinLength (12),
    // so with the current policy those get caught by the length check first, not this one -
    // "administrator" (13 characters) is the only blocklist entry actually long enough to
    // reach and exercise this branch, so it's the one worth pinning here.
    [Theory]
    [InlineData("administrator")]
    [InlineData("ADMINISTRATOR")]
    [InlineData("Administrator")]
    public void IsWeak_ForACommonlyGuessedPasswordThatMeetsTheLengthMinimum_ShouldBeWeakRegardlessOfCase(string password)
    {
        PasswordPolicy.IsWeak(password, "someone", out var reason).Should().BeTrue();
        reason.Should().Contain("commonly guessed");
    }

    [Fact]
    public void IsWeak_ForALongUniquePassword_ShouldNotBeWeak()
    {
        PasswordPolicy.IsWeak("Tr0ub4dor&CorrectHorse", "someone", out var reason).Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void IsWeak_WhenUsernameIsNullOrWhitespace_ShouldSkipTheUsernameCheck()
    {
        PasswordPolicy.IsWeak("Tr0ub4dor&CorrectHorse", null, out _).Should().BeFalse();
        PasswordPolicy.IsWeak("Tr0ub4dor&CorrectHorse", "   ", out _).Should().BeFalse();
    }

    [Fact]
    public void IsWeak_ChecksLengthBeforeTheUsernameMatch_SoAShortUsernameMatchIsReportedAsTooShort()
    {
        // "ab" both matches the username AND is under MinLength - the length check runs
        // first, so the reason should reflect that, not the username-match branch.
        PasswordPolicy.IsWeak("ab", "ab", out var reason).Should().BeTrue();
        reason.Should().Contain("at least");
    }
}
