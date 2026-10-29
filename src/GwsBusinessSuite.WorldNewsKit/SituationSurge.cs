namespace GwsBusinessSuite.WorldNewsKit;

// A story whose coverage at least doubles within a couple of hours, with several new outlets
// picking it up: breaking news, as opposed to a story that's merely big.
public static class SituationSurge
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(2);
    public const int MinimumNewOutlets = 4;

    public static bool IsSurging(int outlets, int outletsBefore) =>
        outlets - outletsBefore >= MinimumNewOutlets && outlets >= 2 * outletsBefore;

    public static string Message(int outlets, int outletsBefore) => outletsBefore == 0
        ? $"Surging: {outlets} outlets picked this story up in the last {Window.TotalHours:0} hours."
        : $"Surging: coverage went from {outletsBefore} to {outlets} outlets in the last {Window.TotalHours:0} hours.";
}
