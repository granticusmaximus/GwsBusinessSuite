using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

public sealed class SshHostKeyPinningTests
{
    [Fact]
    public void IsHostKeyTrusted_ShouldTrust_WhenNothingIsPinnedYet()
    {
        // First-ever connect: no fingerprint stored, so whatever the server presents is
        // accepted (and the caller is expected to pin it immediately afterward).
        SshTerminalService.IsHostKeyTrusted(storedFingerprint: null, incomingFingerprint: "SHA256:abc123").Should().BeTrue();
        SshTerminalService.IsHostKeyTrusted(storedFingerprint: "", incomingFingerprint: "SHA256:abc123").Should().BeTrue();
    }

    [Fact]
    public void IsHostKeyTrusted_ShouldTrust_WhenIncomingFingerprintMatchesPinned()
    {
        SshTerminalService.IsHostKeyTrusted(storedFingerprint: "SHA256:abc123", incomingFingerprint: "SHA256:abc123").Should().BeTrue();
    }

    [Fact]
    public void IsHostKeyTrusted_ShouldReject_WhenIncomingFingerprintDiffersFromPinned()
    {
        // Guards against DigitalOcean IP reuse pointing at a different droplet, or a MITM
        // on the network path - SSH.NET trusts nothing by default, so this must fail closed.
        SshTerminalService.IsHostKeyTrusted(storedFingerprint: "SHA256:abc123", incomingFingerprint: "SHA256:different").Should().BeFalse();
    }
}
