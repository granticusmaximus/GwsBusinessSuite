using System.Security.Cryptography;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;

namespace GwsBusinessSuite.Tests;

// DataProtectionSecretProtector is a thin, intentionally-throwing wrapper - callers
// (DigitalOceanService, SshTerminalService) each catch and handle Unprotect failures
// themselves rather than the protector swallowing them. These tests document and lock in
// that contract rather than changing any behavior.
public sealed class DataProtectionSecretProtectorTests
{
    [Fact]
    public void ProtectThenUnprotect_ShouldRoundTrip()
    {
        var protector = CreateProtector();

        var protectedValue = protector.Protect("super-secret-value");
        var result = protector.Unprotect(protectedValue);

        result.Should().Be("super-secret-value");
        protectedValue.Should().NotBe("super-secret-value");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Protect_ShouldReturnEmptyString_ForNullOrWhitespaceInput(string? input)
    {
        var protector = CreateProtector();

        protector.Protect(input!).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unprotect_ShouldReturnEmptyString_ForNullOrWhitespaceInput(string? input)
    {
        var protector = CreateProtector();

        protector.Unprotect(input!).Should().BeEmpty();
    }

    [Fact]
    public void Unprotect_ShouldThrow_ForCorruptedCiphertext()
    {
        var protector = CreateProtector();

        var action = () => protector.Unprotect("this-is-not-a-valid-protected-payload");

        action.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_ShouldThrow_ForValueProtectedUnderADifferentKeyRing()
    {
        var protectedByOtherKeyRing = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider())
            .Protect("secret");

        var protector = CreateProtector();

        var action = () => protector.Unprotect(protectedByOtherKeyRing);

        action.Should().Throw<CryptographicException>();
    }

    private static DataProtectionSecretProtector CreateProtector() =>
        new(new EphemeralDataProtectionProvider());
}
