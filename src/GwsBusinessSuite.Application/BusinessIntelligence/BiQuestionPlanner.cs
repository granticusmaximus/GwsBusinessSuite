using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.BusinessIntelligence;

// "Ask about my data": turns a plain-English question into one of the fixed report definitions.
// The model only *chooses* among the catalogued source/metric/dimension/range values - it never
// writes a query - and its answer is validated like any builder input. KeywordPlan is the
// deterministic fallback when local Ollama is unavailable or answers something unusable.
public static class BiQuestionPlanner
{
    public static string SystemPrompt(IReadOnlyList<BiQueryShapeDefinition> shapes, IReadOnlyList<int> ranges)
    {
        var catalog = string.Join("\n", shapes.Select(shape =>
            $"- source \"{shape.Value}\" ({shape.Label}: {shape.Description}) metrics: " +
            string.Join(", ", shape.Metrics.Select(m => $"\"{m.Value}\" ({m.Label})")) + "; dimensions: " +
            string.Join(", ", shape.Dimensions.Select(d => $"\"{d.Value}\" ({d.Label})"))));
        return "You map a business question to ONE report definition from this catalog:\n" + catalog +
            $"\nrangeDays must be one of: {string.Join(", ", ranges)}.\n" +
            "Reply with only a JSON object, no prose: " +
            "{\"source\":\"...\",\"metric\":\"...\",\"dimension\":\"...\",\"rangeDays\":30,\"title\":\"short chart title\"}";
    }

    // Returns null when the output names values outside the catalog. Fields are read one by one
    // rather than as strict JSON: small local models often emit nearly-valid JSON (a dropped
    // quote on one key), and one bad field shouldn't throw away the fields that are fine.
    // Catalog values or their labels are both accepted.
    public static BiWidgetEditor? ParseModelAnswer(string output, IReadOnlyList<BiQueryShapeDefinition> shapes, IReadOnlyList<int> ranges)
    {
        string? Read(string name) =>
            Regex.Match(output, $"\"{name}\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } match
                ? match.Groups[1].Value.Trim()
                : null;
        static bool Same(BiOption option, string? value) =>
            value is not null && (string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.Label, value, StringComparison.OrdinalIgnoreCase));

        var source = Read("source");
        var shape = shapes.FirstOrDefault(item => string.Equals(item.Value, source, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Label, source, StringComparison.OrdinalIgnoreCase));
        var metric = shape?.Metrics.FirstOrDefault(item => Same(item, Read("metric")));
        var dimension = shape?.Dimensions.FirstOrDefault(item => Same(item, Read("dimension")));
        if (shape is null || metric is null || dimension is null) return null;

        var days = Regex.Match(output, "\"rangeDays\"\\s*:\\s*\"?(\\d+)", RegexOptions.IgnoreCase) is { Success: true } daysMatch
            && int.TryParse(daysMatch.Groups[1].Value, out var parsed) ? parsed : 30;
        var title = Read("title");
        return new BiWidgetEditor
        {
            Title = Truncate(string.IsNullOrWhiteSpace(title) ? $"{metric.Label} by {dimension.Label}" : title),
            QueryShape = shape.Value,
            Metric = metric.Value,
            Dimension = dimension.Value,
            Visualization = dimension.Value == BiDimensions.Month ? BiVisualizations.Line : BiVisualizations.Bar,
            RangeDays = ranges.Contains(days) ? days : Nearest(ranges, days)
        };
    }

    // Word matching against the catalog labels, plus a few common synonyms.
    public static BiWidgetEditor KeywordPlan(string question, IReadOnlyList<BiQueryShapeDefinition> shapes, IReadOnlyList<int> ranges)
    {
        var text = question.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(text.Contains);
        var source = Has("ticket", "support", "csat", "sla", "satisfaction") ? BiQueryShapes.SupportTickets
            : Has("form", "submission", "lead form", "contact form") ? BiQueryShapes.FormSubmissions
            : Has("automation", "workflow", "run") ? BiQueryShapes.AutomationRuns
            : Has("article", "post", "blog", "view", "visitor", "traffic") ? BiQueryShapes.ArticlePerformance
            : Has("affiliate", "commission", "cj", "advertiser", "sale") ? BiQueryShapes.AffiliateRevenue
            : BiQueryShapes.Deals;
        var shape = shapes.First(item => item.Value == source);

        var metric = shape.Metrics.FirstOrDefault(item => item.Value switch
        {
            BiMetrics.PipelineValue => Has("value", "revenue", "worth", "$", "amount", "pipeline"),
            BiMetrics.Visitors => Has("visitor", "unique", "people"),
            BiMetrics.Commission => Has("commission", "earn"),
            BiMetrics.Sales => Has("sales amount", "sale amount", "revenue"),
            BiMetrics.AverageSatisfaction => Has("csat", "satisfaction", "rating", "happy"),
            BiMetrics.SlaBreaches => Has("sla", "breach", "overdue", "late"),
            _ => false
        }) ?? shape.Metrics[0];

        var dimension = shape.Dimensions.FirstOrDefault(item => item.Value switch
        {
            BiDimensions.Month => Has("month", "trend", "over time", "monthly"),
            BiDimensions.Priority => Has("priority", "urgent"),
            BiDimensions.Workflow => Has("workflow", "which automation"),
            BiDimensions.Form => Has("which form", "by form", "per form"),
            _ => false
        }) ?? shape.Dimensions[0];

        var days = Has("week", "7 days") ? 7
            : Has("quarter", "90 days", "3 months") ? 90
            : Has("year", "12 months", "365") ? 365
            : 30;
        return new BiWidgetEditor
        {
            Title = Truncate(question.Trim().TrimEnd('?')),
            QueryShape = shape.Value,
            Metric = metric.Value,
            Dimension = dimension.Value,
            Visualization = dimension.Value == BiDimensions.Month ? BiVisualizations.Line : BiVisualizations.Bar,
            RangeDays = ranges.Contains(days) ? days : Nearest(ranges, days)
        };
    }

    private static int Nearest(IReadOnlyList<int> ranges, int days) => ranges.OrderBy(range => Math.Abs(range - days)).First();

    private static string Truncate(string value) => value.Length <= 120 ? value : value[..120];
}
