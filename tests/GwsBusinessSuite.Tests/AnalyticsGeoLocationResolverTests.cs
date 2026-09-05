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

    [Theory]
    // Real metadata DatabaseType strings: DB-IP's free City edition, and MaxMind's.
    [InlineData("DBIP-City-Lite", "IP Geolocation by DB-IP", "https://db-ip.com")]
    [InlineData("DBIP-Country-Lite", "IP Geolocation by DB-IP", "https://db-ip.com")]
    [InlineData("GeoLite2-City", "IP Geolocation by MaxMind GeoLite2", "https://www.maxmind.com")]
    public void AttributionFor_CreditsTheVendorTheLoadedDatabaseActuallyCameFrom(
        string databaseType, string expectedText, string expectedUrl)
    {
        // DB-IP Lite is CC-BY 4.0 and requires this link wherever its results are displayed, so
        // crediting the wrong vendor is a licence problem, not a cosmetic one.
        var attribution = AnalyticsGeoLocationResolver.AttributionFor(databaseType);

        attribution.Should().NotBeNull();
        attribution!.Text.Should().Be(expectedText);
        attribution.Url.Should().Be(expectedUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some-Other-Vendor-DB")]
    public void AttributionFor_ClaimsNothingForAnUnrecognizedDatabase(string? databaseType)
    {
        // Better to show no credit than to assert a licence relationship that doesn't exist.
        AnalyticsGeoLocationResolver.AttributionFor(databaseType).Should().BeNull();
    }

    [Fact]
    public void MissingDatabase_ShouldReportNoAttributionToDisplay()
    {
        using var resolver = new AnalyticsGeoLocationResolver(
            Options.Create(new AnalyticsGeoIpOptions
            {
                DatabasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mmdb")
            }),
            NullLogger<AnalyticsGeoLocationResolver>.Instance);

        resolver.IsConfigured.Should().BeFalse();
        resolver.Attribution.Should().BeNull("nothing is being displayed, so nothing needs crediting");
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
