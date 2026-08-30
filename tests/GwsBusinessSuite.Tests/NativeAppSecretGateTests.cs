using FluentAssertions;
using GwsBusinessSuite.Application.NativeApp;

namespace GwsBusinessSuite.Tests;

public sealed class NativeAppSecretGateTests
{
    [Fact]
    public void IsValid_ShouldReturnTrue_ForAMatchingSecret()
    {
        NativeAppSecretGate.IsValid("correct-horse-battery-staple", "correct-horse-battery-staple")
            .Should().BeTrue();
    }

    [Fact]
    public void IsValid_ShouldReturnFalse_ForAWrongSecret()
    {
        NativeAppSecretGate.IsValid("wrong-secret", "correct-horse-battery-staple")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsValid_ShouldReturnFalse_WhenNoSecretIsConfigured(string? configuredSecret)
    {
        NativeAppSecretGate.IsValid("anything", configuredSecret).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsValid_ShouldReturnFalse_WhenNoSecretIsProvided(string? providedSecret)
    {
        NativeAppSecretGate.IsValid(providedSecret, "correct-horse-battery-staple").Should().BeFalse();
    }

    [Fact]
    public void IsValid_ShouldReturnFalse_ForADifferentLengthSecret()
    {
        // A length mismatch must be rejected before FixedTimeEquals ever runs - it requires
        // equal-length inputs and would throw otherwise, not just fail closed.
        NativeAppSecretGate.IsValid("short", "a-much-longer-configured-secret").Should().BeFalse();
    }
}
