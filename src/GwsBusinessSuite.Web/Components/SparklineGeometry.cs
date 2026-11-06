using System.Globalization;

namespace GwsBusinessSuite.Web.Components;

// The points of a sparkline (T5, 06-11-26): values spread evenly across Width, scaled so the
// largest value of either series touches the top and zero sits on the bottom (Height). A previous
// period is drawn on the same scale, so the two are comparable at a glance.
public static class SparklineGeometry
{
    public const double Width = 100;
    public const double Height = 24;
    // Room for the stroke at the top and bottom edges.
    private const double Inset = 1.5;

    public static string Points(IReadOnlyList<double> values, double max)
    {
        if (values.Count == 0) return string.Empty;
        var top = max <= 0 ? 1 : max;
        var step = values.Count == 1 ? 0 : Width / (values.Count - 1);
        return string.Join(' ', values.Select((value, i) => string.Create(CultureInfo.InvariantCulture,
            $"{i * step:0.##},{Height - Inset - Math.Max(0, value) / top * (Height - 2 * Inset):0.##}")));
    }

    public static double Max(IReadOnlyList<double> values, IReadOnlyList<double>? previous) =>
        Math.Max(values.DefaultIfEmpty(0).Max(), previous?.DefaultIfEmpty(0).Max() ?? 0);

    // "Last 14 days: 23 in all, 3 today" for screen readers and the hover title.
    public static string Describe(string label, IReadOnlyList<double> values, Func<double, string> format, IReadOnlyList<double>? previous = null)
    {
        if (values.Count == 0) return $"{label}: no data";
        var text = $"{label}: {format(values.Sum())} in all, {format(values[^1])} on the last day";
        return previous is { Count: > 0 } ? $"{text}; {format(previous.Sum())} in the period before" : text;
    }
}
