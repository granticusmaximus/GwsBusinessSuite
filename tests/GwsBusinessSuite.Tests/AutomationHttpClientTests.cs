using System.Net;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// Regression guard for a real finding: the SSRF guard on workflow HTTP nodes originally
// validated only the request's original URL, once, before sending - a redirect to an
// internal address (cloud metadata, localhost) sailed through untouched, and the IPv6
// checks had real gaps (no Unique Local Address coverage, no IPv4-mapped-IPv6 unwrapping).
// The fix moved validation into a SocketsHttpHandler.ConnectCallback (re-checked on every
// real TCP connection, including ones opened to follow a redirect) - these tests exercise
// the pure address-classification logic that callback relies on, since exercising the
// callback itself would require a real socket connection.
public sealed class AutomationHttpClientTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud metadata endpoint
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")] // multicast/reserved
    public void IsPrivateOrReservedAddress_ShouldRejectPrivateAndReservedIPv4(string ip)
    {
        Assert.True(AutomationDestinationValidator.IsPrivateOrReservedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    public void IsPrivateOrReservedAddress_ShouldAllowPublicIPv4(string ip)
    {
        Assert.False(AutomationDestinationValidator.IsPrivateOrReservedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("::1")] // loopback
    [InlineData("fe80::1")] // link-local
    [InlineData("fc00::1")] // unique local, low end of fc00::/7
    [InlineData("fd12:3456:789a::1")] // unique local, fd00::/8 (Docker's common default)
    public void IsPrivateOrReservedAddress_ShouldRejectPrivateIPv6(string ip)
    {
        Assert.True(AutomationDestinationValidator.IsPrivateOrReservedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped loopback
    [InlineData("::ffff:10.0.0.5")] // IPv4-mapped private
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped cloud metadata
    public void IsPrivateOrReservedAddress_ShouldUnwrapIPv4MappedAddressesBeforeChecking(string ip)
    {
        Assert.True(AutomationDestinationValidator.IsPrivateOrReservedAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsPrivateOrReservedAddress_ShouldAllowPublicIPv6()
    {
        Assert.False(AutomationDestinationValidator.IsPrivateOrReservedAddress(IPAddress.Parse("2606:4700:4700::1111")));
    }
}
