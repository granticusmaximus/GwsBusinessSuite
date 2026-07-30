using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class AnalyticsGeoLocationResolverTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.20.30.40")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    public void IsPublicAddress_ShouldRejectNonPublicAddresses(string value)
    {
        AnalyticsGeoLocationResolver.IsPublicAddress(IPAddress.Parse(value)).Should().BeFalse();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void IsPublicAddress_ShouldAcceptPublicAddresses(string value)
    {
        AnalyticsGeoLocationResolver.IsPublicAddress(IPAddress.Parse(value)).Should().BeTrue();
    }

    [Fact]
    public void MissingDatabase_ShouldDisableResolverWithoutBlockingStartup()
    {
        using var resolver = new AnalyticsGeoLocationResolver(
            Options.Create(new AnalyticsGeoIpOptions
            {
                DatabasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mmdb")
            }),
            NullLogger<AnalyticsGeoLocationResolver>.Instance);

        resolver.IsConfigured.Should().BeFalse();
        resolver.Resolve(IPAddress.Parse("8.8.8.8")).Should().BeNull();
    }
}
