using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.SuiteSearch;

// SentinelGPT in ⌘K (tech-forward T1b): a request in plain words ("unpaid invoices for Dana",
// "urgent tickets this week") becomes one structured search through a find_records tool call,
// which SuiteSearchService.FindAsync runs against the real records. The model only chooses the
// filters; it never writes the results.
public sealed record SuiteSearchRequest(string Kind, string Text, string? Status, bool OverdueOnly, int? WithinDays, string? Contact, string Summary, string? Priority = null);

public sealed record SuiteSearchAnswer(string Summary, IReadOnlyList<SuiteSearchResult> Results);

public interface ISuiteSearchAssistant
{
    // Null when the request isn't something it can search for, or SentinelGPT is unavailable.
    Task<SuiteSearchAnswer?> AskAsync(string question, string performedBy, CancellationToken ct = default);
}

public static class SuiteSearchRequestRules
{
    public const string ToolName = "find_records";
    public const int MaxQuestionLength = 200;

    public static readonly string[] Kinds = ["contacts", "deals", "tickets", "invoices", "posts", "pages", "workflows"];

    public const string ToolDescription = "Find the admin's business records that match their request.";

    public const string ToolSchema = """
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["contacts", "deals", "tickets", "invoices", "posts", "pages", "workflows"], "description": "Which records to find." },
            "text": { "type": "string", "description": "Words to match in the record's name, title or subject. Empty when the request names no such words." },
            "status": { "type": "string", "description": "A status to match: Open, Pending, Resolved, Closed (tickets); Draft, Sent, Paid, Void (invoices); Lead, Prospect, Customer, Inactive (contacts); Draft, PendingReview, Published (posts); Draft, Published (pages); Active, Inactive (workflows); or a deal's stage." },
            "overdue": { "type": "boolean", "description": "Only records past due: sent invoices past their due date, open tickets past their resolution time, contacts whose follow-up is due." },
            "within_days": { "type": "integer", "description": "Only records created in the last N days (today 1, this week 7, this month 30)." },
            "priority": { "type": "string", "enum": ["Urgent", "High", "Normal", "Low"], "description": "Tickets only: their priority." },
            "contact": { "type": "string", "description": "Only tickets, invoices or deals belonging to a contact whose name contains this." },
            "summary": { "type": "string", "description": "A short plain description of the search, such as: Unpaid invoices for Dana Rivers." }
          },
          "required": ["kind", "summary"]
        }
        """;

    // Plain-language requests only: three or more words, or a question. Type filters (t:) and
    // short lookups stay with the keyword search, which is instant.
    public static bool LooksLikeRequest(string? query)
    {
        var text = (query ?? string.Empty).Trim();
        if (text.Length is < 3 or > MaxQuestionLength) return false;
        if (SuiteSearchQuery.Parse(text).IsFiltered) return false;
        return text.EndsWith('?') || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    }

    // The model's arguments, checked: an unknown kind or unreadable JSON means no search, and
    // every string is trimmed and capped before it reaches a query.
    public static SuiteSearchRequest? FromToolArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var kind = Text(root, "kind", 20)?.ToLowerInvariant();
            if (kind is null || !Kinds.Contains(kind)) return null;
            var days = Number(root, "within_days");
            return new SuiteSearchRequest(
                kind,
                Text(root, "text", 80) ?? string.Empty,
                NormalizeStatus(kind, Text(root, "status", 30)),
                Flag(root, "overdue"),
                days is >= 1 ? Math.Min(days.Value, 365) : null,
                Text(root, "contact", 80),
                Text(root, "summary", 100) ?? "SentinelGPT's search",
                kind == "tickets" ? NormalizePriority(Text(root, "priority", 20)) : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Each kind's real statuses, and the words people use for them. A status the model invents
    // ("Unpaid", "Urgent") would match nothing and hide every record, so anything not recognised
    // here is dropped rather than searched for.
    private static readonly Dictionary<string, string[]> Statuses = new(StringComparer.Ordinal)
    {
        ["tickets"] = ["Open", "Pending", "Resolved", "Closed"],
        ["invoices"] = ["Draft", "Sent", "Paid", "Void"],
        ["contacts"] = ["Lead", "Prospect", "Customer", "Inactive"],
        ["deals"] = ["Lead", "Qualified", "ProposalSent", "Negotiation", "Won", "Lost"],
        ["posts"] = ["Draft", "PendingReview", "Published"],
        ["pages"] = ["Draft", "Published"],
        ["workflows"] = ["Active", "Inactive"],
    };

    private static readonly Dictionary<string, Dictionary<string, string>> StatusWords = new(StringComparer.Ordinal)
    {
        ["tickets"] = new(StringComparer.OrdinalIgnoreCase) { ["unresolved"] = "Open", ["new"] = "Open", ["active"] = "Open", ["waiting"] = "Pending", ["on hold"] = "Pending", ["solved"] = "Resolved", ["fixed"] = "Resolved", ["done"] = "Resolved" },
        ["invoices"] = new(StringComparer.OrdinalIgnoreCase) { ["unpaid"] = "Sent", ["outstanding"] = "Sent", ["open"] = "Sent", ["due"] = "Sent", ["owed"] = "Sent", ["settled"] = "Paid", ["cancelled"] = "Void", ["canceled"] = "Void", ["voided"] = "Void" },
        ["contacts"] = new(StringComparer.OrdinalIgnoreCase) { ["leads"] = "Lead", ["prospects"] = "Prospect", ["customers"] = "Customer", ["client"] = "Customer", ["clients"] = "Customer" },
        ["deals"] = new(StringComparer.OrdinalIgnoreCase) { ["proposal"] = "ProposalSent", ["proposal sent"] = "ProposalSent", ["negotiating"] = "Negotiation", ["closed won"] = "Won", ["closed lost"] = "Lost" },
        ["posts"] = new(StringComparer.OrdinalIgnoreCase) { ["live"] = "Published", ["in review"] = "PendingReview", ["pending review"] = "PendingReview", ["pending"] = "PendingReview", ["drafts"] = "Draft" },
        ["pages"] = new(StringComparer.OrdinalIgnoreCase) { ["live"] = "Published", ["drafts"] = "Draft" },
        ["workflows"] = new(StringComparer.OrdinalIgnoreCase) { ["enabled"] = "Active", ["on"] = "Active", ["disabled"] = "Inactive", ["off"] = "Inactive", ["paused"] = "Inactive" },
    };

    public static string? NormalizeStatus(string kind, string? status)
    {
        if (status is null || !Statuses.TryGetValue(kind, out var statuses)) return null;
        var exact = statuses.FirstOrDefault(known => string.Equals(known, status, StringComparison.OrdinalIgnoreCase)
            || string.Equals(known, status.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        return StatusWords.TryGetValue(kind, out var words) && words.TryGetValue(status, out var mapped) ? mapped : null;
    }

    private static readonly Dictionary<string, string> Priorities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["urgent"] = "Urgent", ["critical"] = "Urgent", ["high"] = "High", ["important"] = "High",
        ["normal"] = "Normal", ["medium"] = "Normal", ["low"] = "Low",
    };

    // The model sometimes leaves out a filter the request plainly states ("open tickets this
    // week" without status). These read it back from the request's own words: a status or
    // priority word for the kind, and "for/from <Name>" for whose records they are.
    public static SuiteSearchRequest FillFromQuestion(SuiteSearchRequest request, string question)
    {
        var words = Regex.Split(question.ToLowerInvariant(), @"[^a-z]+").Where(word => word.Length > 0).ToArray();
        var phrase = " " + string.Join(' ', words) + " ";
        string? WordStatus()
        {
            if (Statuses.TryGetValue(request.Kind, out var known))
                foreach (var status in known)
                    if (phrase.Contains($" {status.ToLowerInvariant()} ")) return status;
            if (StatusWords.TryGetValue(request.Kind, out var map))
                foreach (var (word, status) in map)
                    if (phrase.Contains($" {word} ")) return status;
            return null;
        }
        string? WordPriority() => request.Kind == "tickets" ? words.Select(NormalizePriority).FirstOrDefault(priority => priority is not null) : null;
        return request with { Status = request.Status ?? WordStatus(), Priority = request.Priority ?? WordPriority() };
    }

    private static readonly Regex OwnerPhrase = new(@"\b(?:for|from|with|of)\s+([A-Z][\w'-]+(?:\s+[A-Z][\w'-]+)?)", RegexOptions.CultureInvariant);

    // A capitalised name after for/from/with/of ("invoices for Dana Rivers"), for kinds that
    // belong to a contact, when the model gave neither a contact nor text.
    public static string? OwnerFromQuestion(SuiteSearchRequest request, string question) =>
        request.Kind is "tickets" or "invoices" or "deals" && request.Contact is null && request.Text.Length == 0
            && OwnerPhrase.Match(question) is { Success: true } match ? match.Groups[1].Value : null;

    public static string? NormalizePriority(string? priority) =>
        priority is not null && Priorities.TryGetValue(priority, out var known) ? known : null;

    private static string? Text(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim() ?? string.Empty;
        return text.Length == 0 ? null : text.Length <= max ? text : text[..max];
    }

    private static int? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var flag) && flag));
}
