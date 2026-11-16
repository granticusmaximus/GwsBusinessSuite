namespace GwsBusinessSuite.Application.Abstractions;

public enum LocalAiBatteryMode
{
    Balanced,
    Always,
    Never,
}

public enum LocalAiThermalState
{
    Unknown,
    Nominal,
    Fair,
    Serious,
    Critical,
}

public sealed record LocalAiPowerSnapshot(bool? OnBattery, double? BatteryLevel, LocalAiThermalState ThermalState);

public sealed record LocalAiPowerDecision(bool KeepAwake, string Reason);

public static class LocalAiBatteryPolicy
{
    public const double BalancedMinimumBattery = 0.20;

    public static LocalAiPowerDecision Decide(LocalAiBatteryMode mode, LocalAiPowerSnapshot power)
    {
        if (power.ThermalState is LocalAiThermalState.Serious or LocalAiThermalState.Critical)
            return new(false, "Mac is too warm; idle sleep is allowed and the droplet takes over when this Mac becomes unavailable.");

        if (power.OnBattery is false)
            return new(true, "Plugged in; this Mac stays available for local AI.");

        if (power.OnBattery is null)
            return new(true, "Power source is unavailable; this Mac stays available for local AI.");

        if (mode == LocalAiBatteryMode.Never)
            return new(false, "On battery; idle sleep is allowed and the droplet takes over when this Mac becomes unavailable.");

        if (mode == LocalAiBatteryMode.Balanced && power.BatteryLevel is { } level && level < BalancedMinimumBattery)
            return new(false, $"Battery is {level:P0}; idle sleep is allowed below 20% and the droplet takes over when this Mac becomes unavailable.");

        return new(true, power.BatteryLevel is { } battery
            ? $"On battery at {battery:P0}; this Mac stays available for local AI."
            : "On battery; this Mac stays available for local AI.");
    }
}
