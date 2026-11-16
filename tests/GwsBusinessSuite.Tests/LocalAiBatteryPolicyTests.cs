using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class LocalAiBatteryPolicyTests
{
    [Theory]
    [InlineData(LocalAiBatteryMode.Balanced, 0.75, true)]
    [InlineData(LocalAiBatteryMode.Balanced, 0.19, false)]
    [InlineData(LocalAiBatteryMode.Always, 0.05, true)]
    [InlineData(LocalAiBatteryMode.Never, 0.90, false)]
    public void BatteryModes_ShouldApplyTheirAvailabilityPolicy(LocalAiBatteryMode mode, double level, bool expected)
    {
        LocalAiBatteryPolicy.Decide(mode, new(true, level, LocalAiThermalState.Nominal)).KeepAwake.Should().Be(expected);
    }

    [Fact]
    public void PluggedIn_ShouldStayAvailableRegardlessOfBatteryMode()
    {
        LocalAiBatteryPolicy.Decide(LocalAiBatteryMode.Never, new(false, 0.05, LocalAiThermalState.Nominal))
            .KeepAwake.Should().BeTrue();
    }

    [Theory]
    [InlineData(LocalAiThermalState.Serious)]
    [InlineData(LocalAiThermalState.Critical)]
    public void UnsafeThermalState_ShouldReleaseTheAssertion(LocalAiThermalState thermal)
    {
        LocalAiBatteryPolicy.Decide(LocalAiBatteryMode.Always, new(true, 0.90, thermal)).KeepAwake.Should().BeFalse();
    }

    [Fact]
    public void MissingBatteryTelemetry_ShouldNotDisableDesktopMacs()
    {
        LocalAiBatteryPolicy.Decide(LocalAiBatteryMode.Balanced, new(null, null, LocalAiThermalState.Unknown))
            .KeepAwake.Should().BeTrue();
    }
}
