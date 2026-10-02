using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace GwsBusinessSuite.Tests;

public sealed class SharedSmtpOptionsTests
{
    private static IConfigurationSection Shared(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(kv => $"Smtp:{kv.Key}", kv => kv.Value))
            .Build()
            .GetSection(SharedSmtpOptions.SectionName);

    private static readonly Dictionary<string, string?> Mailgun = new()
    {
        ["Host"] = "smtp.example.com", ["Port"] = "465", ["Security"] = "SslOnConnect",
        ["Username"] = "postmaster", ["Password"] = "secret", ["FromAddress"] = "noreply@example.com"
    };

    [Fact]
    public void FeatureWithNoServer_InheritsTheSharedSmtpSection()
    {
        var options = new GrowthReportEmailOptions();

        SharedSmtpOptions.ApplyFallback(options, Shared(Mailgun));

        options.Host.Should().Be("smtp.example.com");
        options.Port.Should().Be(465);
        options.Security.Should().Be("SslOnConnect");
        options.Username.Should().Be("postmaster");
        options.Password.Should().Be("secret");
        options.FromAddress.Should().Be("noreply@example.com");
        options.FromName.Should().Be("GWS Growth Studio", "display names stay per feature");
    }

    [Fact]
    public void FeatureWithItsOwnServer_KeepsIt_AndOnlyBorrowsAMissingFromAddress()
    {
        var options = new BookingEmailOptions { Host = "smtp.bookings.test", Port = 2525, Username = "bookings" };

        SharedSmtpOptions.ApplyFallback(options, Shared(Mailgun));

        options.Host.Should().Be("smtp.bookings.test");
        options.Port.Should().Be(2525);
        options.Username.Should().Be("bookings");
        options.FromAddress.Should().Be("noreply@example.com");
    }

    [Fact]
    public void NoSharedSection_LeavesTheFeatureUnconfigured()
    {
        var options = new ClientPortalEmailOptions();

        SharedSmtpOptions.ApplyFallback(options, Shared([]));

        options.Host.Should().BeEmpty();
        options.FromAddress.Should().BeEmpty();
        options.Port.Should().Be(587);
    }
}
