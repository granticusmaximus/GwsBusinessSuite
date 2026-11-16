using GwsBusinessSuite.Application.Abstractions;

#if MACCATALYST
using Foundation;
using UIKit;
#endif

namespace GwsBusinessSuite.App;

public sealed class LocalAiPowerPolicyService
{
    public const string PreferenceKey = "LocalAiBatteryMode";
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(15);
    private bool _started;

    public LocalAiBatteryMode Mode { get; private set; } = ReadMode();
    public LocalAiPowerDecision Decision { get; private set; } = new(false, "Local AI power policy has not started.");

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = MonitorAsync();
    }

    public void SetMode(LocalAiBatteryMode mode)
    {
        Mode = mode;
        Preferences.Default.Set(PreferenceKey, mode.ToString());
        Refresh();
    }

    public void Refresh()
    {
#if MACCATALYST
        Decision = LocalAiBatteryPolicy.Decide(Mode, Snapshot());
        MacAiServer.SetLocalAiAvailable(Decision.KeepAwake);
#else
        Decision = new(false, "Local AI battery control is available on macOS.");
#endif
    }

    private async Task MonitorAsync()
    {
        while (true)
        {
            Refresh();
            await Task.Delay(RefreshEvery);
        }
    }

    private static LocalAiBatteryMode ReadMode() =>
        Enum.TryParse<LocalAiBatteryMode>(Preferences.Default.Get(PreferenceKey, nameof(LocalAiBatteryMode.Balanced)), out var mode)
            ? mode
            : LocalAiBatteryMode.Balanced;

#if MACCATALYST
    private static LocalAiPowerSnapshot Snapshot()
    {
        var device = UIDevice.CurrentDevice;
        device.BatteryMonitoringEnabled = true;
        var onBattery = device.BatteryState switch
        {
            UIDeviceBatteryState.Unplugged => true,
            UIDeviceBatteryState.Charging or UIDeviceBatteryState.Full => false,
            _ => (bool?)null,
        };
        var level = device.BatteryLevel is >= 0 and <= 1 ? device.BatteryLevel : (float?)null;
        var thermal = NSProcessInfo.ProcessInfo.ThermalState.ToString() switch
        {
            "Nominal" => LocalAiThermalState.Nominal,
            "Fair" => LocalAiThermalState.Fair,
            "Serious" => LocalAiThermalState.Serious,
            "Critical" => LocalAiThermalState.Critical,
            _ => LocalAiThermalState.Unknown,
        };
        return new(onBattery, level, thermal);
    }
#endif
}
