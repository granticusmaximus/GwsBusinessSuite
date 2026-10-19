using System.Globalization;

namespace GwsBusinessSuite.Application.BusinessIntelligence;

// The plain-English answer shown above an asked question's chart. Written from the computed
// result only, never by the model, so every number in it is one the chart shows.
public static class BiAnswerSummary
{
    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    public static string Describe(BiWidgetEditor plan, BiChartResult result)
    {
        var window = $"the last {plan.RangeDays} days";
        var points = result.Points.Where(point => point.Value != 0).ToList();
        if (points.Count == 0 && result.Total == 0)
        {
            return $"Nothing recorded for {result.MetricLabel.ToLowerInvariant()} in {window}.";
        }

        var isRating = result.ValueFormat == "Rating";
        var sentences = new List<string> { $"{result.MetricLabel} over {window}: {Format(result.Total, result.ValueFormat)}{Change(result, plan.RangeDays, isRating)}." };

        if (points.Count > 1 && plan.Dimension == BiDimensions.Month)
        {
            var high = points.MaxBy(point => point.Value)!;
            var low = points.MinBy(point => point.Value)!;
            sentences.Add($"Highest in {high.Label} ({Format(high.Value, result.ValueFormat)}), lowest in {low.Label} ({Format(low.Value, result.ValueFormat)}).");
        }
        else if (points.Count > 1)
        {
            var ranked = points.OrderByDescending(point => point.Value).ToList();
            if (ranked[0].Value == ranked[1].Value)
            {
                sentences.Add($"{ranked[0].Label} and {ranked[1].Label} tie at {Format(ranked[0].Value, result.ValueFormat)} each.");
            }
            else
            {
                var share = !isRating && result.Total > 0 ? $" ({(ranked[0].Value / result.Total).ToString("P0", UsCulture)})" : string.Empty;
                sentences.Add($"{ranked[0].Label} leads with {Format(ranked[0].Value, result.ValueFormat)}{share}, then {ranked[1].Label} with {Format(ranked[1].Value, result.ValueFormat)}.");
            }
        }
        else if (points.Count == 1)
        {
            sentences.Add($"All of it is {points[0].Label}.");
        }

        return string.Join(' ', sentences);
    }

    public static string Format(decimal value, string format) => format switch
    {
        "Currency" => value.ToString("C2", UsCulture),
        "Rating" => value.ToString("0.0#", UsCulture) + " / 5",
        _ => value.ToString("N0", UsCulture)
    };

    private static string Change(BiChartResult result, int days, bool isRating)
    {
        var before = $"the {days} days before";
        if (result.PreviousTotal == 0)
        {
            return isRating ? string.Empty : $", none in {before}";
        }
        if (isRating)
        {
            return $" (was {Format(result.PreviousTotal, result.ValueFormat)} in {before})";
        }
        var change = (result.Total - result.PreviousTotal) / result.PreviousTotal;
        return Math.Abs(change) < 0.005m
            ? $", level with {before}"
            : $", {(change > 0 ? "up" : "down")} {Math.Abs(change).ToString("P0", UsCulture)} on {before}";
    }
}
