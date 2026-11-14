using GwsBusinessSuite.Application.CameraIntel;

namespace GwsBusinessSuite.Application.LiveLayers;

// #6 (13-11-26): history for Overwatch's live layers, so the time machine can replay aircraft,
// ships and power outages as it does incidents. Like incident history, only what Overwatch loaded
// is kept (the views people looked at), one sample per object every few minutes, for 72 hours.
public sealed record LiveTrack(string Kind, string Id, string Title, double Latitude, double Longitude, double? Heading, DateTimeOffset At);

public interface ILiveTrackHistory
{
    void Record(string kind, IEnumerable<LiveTrack> tracks);
    // Each object's last sample in the view within its kind's tolerance before `at`.
    IReadOnlyList<LiveTrack> At(BoundingBox bounds, DateTimeOffset at, int maxPerKind = LiveTrackKinds.MaxPerKind);
}

public static class LiveTrackKinds
{
    public const string Aircraft = "aircraft";
    public const string Ship = "ship";
    public const string Outage = "outage";
    public const int MaxPerKind = 1500;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(72);

    public static readonly IReadOnlyList<string> All = [Aircraft, Ship, Outage];

    // How often an object is sampled, and how far back a sample still stands for "then".
    public static TimeSpan SampleEvery(string kind) => kind == Outage ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(5);
    public static TimeSpan Tolerance(string kind) => kind switch
    {
        Aircraft => TimeSpan.FromMinutes(10),
        Ship => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromMinutes(45),
    };
}
