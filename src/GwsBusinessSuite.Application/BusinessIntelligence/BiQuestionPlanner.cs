using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.BusinessIntelligence;

// "Ask about my data": turns a plain-English question into one of the fixed report definitions.
// The model only *chooses* among the catalogued source/metric/dimension/range values - it never
// writes a query - and its answer is validated like any builder input. KeywordPlan is the
// deterministic fallback when local Ollama is unavailable or answers something unusable.
public static class BiQuestionPlanner
{
    // The installed model to plan with: general chat models first (gemma and qwen follow the
    // JSON format more reliably than llama3.2), never an embedding model, which can't generate.
    public static string? PickModel(IEnumerable<string> installed)
    {
        var usable = installed.Where(name => !name.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
        string? Find(string prefix) => usable.FirstOrDefault(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return Find("gemma") ?? Find("qwen") ?? Find("llama3") ?? Find("mistral") ?? Find("phi") ?? usable.FirstOrDefault();
    }

    // previous: the last answer's report, so a follow-up like "now by month" can refine it.
    public static string SystemPrompt(IReadOnlyList<BiQueryShapeDefinition> shapes, IReadOnlyList<int> ranges, BiWidgetEditor? previous = null)
    {
        var catalog = string.Join("\n", shapes.Select(shape =>
            $"- source \"{shape.Value}\" ({shape.Label}: {shape.Description}) metrics: " +
            string.Join(", ", shape.Metrics.Select(m => $"\"{m.Value}\" ({m.Label})")) + "; dimensions: " +
            string.Join(", ", shape.Dimensions.Select(d => $"\"{d.Value}\" ({d.Label})"))));
        return "You map a business question to ONE report definition from this catalog:\n" + catalog +
            $"\nrangeDays must be one of: {string.Join(", ", ranges)}.\n" +
            "Reply with only a JSON object, no prose: " +
            "{\"source\":\"...\",\"metric\":\"...\",\"dimension\":\"...\",\"rangeDays\":30,\"title\":\"short chart title\"}" +
            (previous is null ? string.Empty :
                $"\nThe previous answer used {{\"source\":\"{previous.QueryShape}\",\"metric\":\"{previous.Metric}\",\"dimension\":\"{previous.Dimension}\",\"rangeDays\":{previous.RangeDays}}}. " +
                "If the new question only refines it (another breakdown or time range), keep every field it doesn't change.");
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
    // A question that names no data source refines the previous answer when there is one
    // ("now by month", "last 90 days"): whatever it doesn't mention stays as it was.
    public static BiWidgetEditor KeywordPlan(string question, IReadOnlyList<BiQueryShapeDefinition> shapes, IReadOnlyList<int> ranges, BiWidgetEditor? previous = null)
    {
        var text = question.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(text.Contains);
        var named = Has("ticket", "support", "csat", "sla", "satisfaction") ? BiQueryShapes.SupportTickets
            : Has("form", "submission", "lead form", "contact form") ? BiQueryShapes.FormSubmissions
            : Has("automation", "workflow", "run") ? BiQueryShapes.AutomationRuns
            : Has("article", "post", "blog", "view", "visitor", "traffic") ? BiQueryShapes.ArticlePerformance
            : Has("affiliate", "commission", "cj", "advertiser", "sale") ? BiQueryShapes.AffiliateRevenue
            : Has("deal", "pipeline", "stage", "won", "lost") ? BiQueryShapes.Deals
            : null;
        var followUp = named is null && previous is not null && shapes.Any(item => item.Value == previous.QueryShape);
        var shape = shapes.First(item => item.Value == (named ?? (followUp ? previous!.QueryShape : BiQueryShapes.Deals)));

        var metric = shape.Metrics.FirstOrDefault(item => item.Value switch
        {
            BiMetrics.PipelineValue => Has("value", "revenue", "worth", "$", "amount", "pipeline"),
            BiMetrics.Visitors => Has("visitor", "unique", "people"),
            BiMetrics.Commission => Has("commission", "earn"),
            BiMetrics.Sales => Has("sales amount", "sale amount", "revenue"),
            BiMetrics.AverageSatisfaction => Has("csat", "satisfaction", "rating", "happy"),
            BiMetrics.SlaBreaches => Has("sla", "breach", "overdue", "late"),
            _ => false
        }) ?? (followUp ? shape.Metrics.FirstOrDefault(item => item.Value == previous!.Metric) : null) ?? shape.Metrics[0];

        var dimension = shape.Dimensions.FirstOrDefault(item => item.Value switch
        {
            // "this month" is a time range, not a breakdown.
            BiDimensions.Month => Has("by month", "per month", "each month", "monthly", "trend", "over time"),
            BiDimensions.Priority => Has("priority", "urgent"),
            BiDimensions.Workflow => Has("workflow", "which automation"),
            BiDimensions.Form => Has("which form", "by form", "per form"),
            _ => false
        }) ?? (followUp ? shape.Dimensions.FirstOrDefault(item => item.Value == previous!.Dimension) : null) ?? shape.Dimensions[0];

        int? askedDays = Has("week", "7 days") ? 7
            : Has("quarter", "90 days", "3 months") ? 90
            : Has("year", "12 months", "365") ? 365
            : Has("30 days", "this month", "last month") ? 30
            : null;
        var days = askedDays ?? (followUp ? previous!.RangeDays : 30);
        return new BiWidgetEditor
        {
            // A follow-up's own wording ("now by month") makes a poor chart title.
            Title = followUp ? $"{metric.Label} by {dimension.Label}" : Truncate(question.Trim().TrimEnd('?')),
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
