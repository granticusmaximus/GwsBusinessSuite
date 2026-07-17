using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Web.Services;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class LiveShowIceServerProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateConfiguration_WithoutTurnSecret_ReturnsStunFallbackOnly()
    {
        var options = new LiveShowIceOptions
        {
            StunUrls = ["stun:one.example:3478", "not-an-ice-url"],
            Turn = new LiveShowTurnOptions { Urls = ["turn:relay.example:3478"] }
        };

        var provider = CreateProvider(options);
        var result = provider.CreateConfiguration();

        provider.IsTurnConfigured.Should().BeFalse();
        result.IsTurnConfigured.Should().BeFalse();
        result.TurnCredentialsExpireAt.Should().BeNull();
        result.IceServers.Should().ContainSingle()
            .Which.Urls.Should().Equal("stun:one.example:3478");
    }

    [Fact]
    public void CreateConfiguration_WithTurnSettings_MintsCoturnRestCredential()
    {
        const string secret = "a-long-test-only-shared-secret-12345";
        var options = new LiveShowIceOptions
        {
            StunUrls = ["stun:stun.example:3478"],
            Turn = new LiveShowTurnOptions
            {
                Urls =
                [
                    "turn:relay.example:3478?transport=udp",
                    "turn:relay.example:3478?transport=tcp"
                ],
                SharedSecret = secret,
                CredentialLifetimeMinutes = 30
            }
        };

        var provider = CreateProvider(options);
        var result = provider.CreateConfiguration();

        provider.IsTurnConfigured.Should().BeTrue();
        result.IsTurnConfigured.Should().BeTrue();
        result.TurnCredentialsExpireAt.Should().Be(Now.AddMinutes(30));
        result.IceServers.Should().HaveCount(2);

        var turn = result.IceServers[1];
        turn.Urls.Should().Equal(options.Turn.Urls);
        turn.Username.Should().StartWith($"{Now.AddMinutes(30).ToUnixTimeSeconds()}:gws-live-");

        var expectedCredential = Convert.ToBase64String(HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(turn.Username!)));
        turn.Credential.Should().Be(expectedCredential);
    }

    [Fact]
    public void CreateConfiguration_ClampsUnsafeCredentialLifetime()
    {
        var options = new LiveShowIceOptions
        {
            Turn = new LiveShowTurnOptions
            {
                Urls = ["turn:relay.example:3478"],
                SharedSecret = "test-secret-that-is-at-least-32-characters",
                CredentialLifetimeMinutes = 1
            }
        };

        var result = CreateProvider(options).CreateConfiguration();

        result.TurnCredentialsExpireAt.Should().Be(Now.AddMinutes(5));
    }

    private static LiveShowIceServerProvider CreateProvider(LiveShowIceOptions options) =>
        new(Options.Create(options), new FixedTimeProvider(Now));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
