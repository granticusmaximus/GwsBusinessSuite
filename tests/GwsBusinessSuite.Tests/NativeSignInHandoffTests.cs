using FluentAssertions;
using GwsBusinessSuite.Web.Security;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Tests;

// The Mac app's device sign-in carried into its web view: a one-time, one-minute code.
public sealed class NativeSignInHandoffTests
{
    [Fact]
    public void Code_ShouldWorkOnceForTheUserItWasIssuedTo()
    {
        var handoff = new NativeSignInHandoff(new MemoryCache(new MemoryCacheOptions()));
        var code = handoff.Issue("grant");
        code.Should().HaveLength(64);
        handoff.Issue("grant").Should().NotBe(code);

        handoff.Redeem(code.ToUpperInvariant()).Should().Be("grant");
        handoff.Redeem(code).Should().BeNull("a code works once");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void UnknownCodes_ShouldSignNobodyIn(string? code) =>
        new NativeSignInHandoff(new MemoryCache(new MemoryCacheOptions())).Redeem(code).Should().BeNull();

    [Fact]
    public void Code_ShouldExpireAfterAMinute()
    {
        var clock = new ManualClock { Now = DateTimeOffset.Parse("2026-10-18T12:00:00Z") };
        var handoff = new NativeSignInHandoff(new MemoryCache(new MemoryCacheOptions { Clock = clock }));
        var code = handoff.Issue("grant");
        clock.Now += NativeSignInHandoff.Lifetime + TimeSpan.FromSeconds(1);
        handoff.Redeem(code).Should().BeNull();
    }

    private sealed class ManualClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset Now { get; set; }
        public DateTimeOffset UtcNow => Now;
    }
}
