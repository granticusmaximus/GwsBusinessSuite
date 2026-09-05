namespace GwsBusinessSuite.Application.Automation;

// Built-in "New from template" starter gallery - mirrors SentinelStarterTemplates.cs's own
// pattern exactly (Wiki/SentinelStarterTemplates.cs): fixed, in-memory, never persisted as
// database rows, distinct from the user-generated AutomationWorkflowTemplate CRUD
// (AutomationTemplateService). Instantiated through the same
// IAutomationWorkflowService.CreateFromGraphAsync used for user templates and import, so no new
// plumbing was needed - only this data plus a "Starter workflows" section in the UI's
// "New from template" picker.
public sealed record AutomationStarterWorkflow(string Key, string Icon, string Description, AutomationWorkflowGraphPackage Graph);

public static class AutomationStarterWorkflows
{
    public static readonly IReadOnlyList<AutomationStarterWorkflow> All =
    [
        new("crm-deal-won-notify", "bi-graph-up-arrow",
            "Emails someone when a watched database row changes. Fill in the database id, add a Stage-equals-Won condition, and set a recipient after creating it.",
            CrmDealWonNotifyGraph()),
        new("scheduled-digest", "bi-calendar-week",
            "Every Monday at 9am, builds a short message and emails it - a starting point for a weekly digest or report.",
            ScheduledDigestGraph()),
        new("webhook-relay", "bi-arrow-left-right",
            "Receives a webhook and forwards its payload to another URL - a starting point for connecting two external systems.",
            WebhookRelayGraph()),
        new("local-events-scrape", "bi-calendar-range",
            "Every 6 hours, fetches a public events page and pulls out its listings - change the URL to add any venue, chamber or city calendar to your local watch. Add a Database Row node to keep them.",
            LocalEventsScrapeGraph())
    ];

    public static AutomationStarterWorkflow? Find(string key) =>
        All.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));

    // The point of shipping this one is that adding a new event source becomes "copy this, change
    // the URL" instead of new C# per site. Any page publishing schema.org Event markup works,
    // which is most venue, chamber and city calendars.
    private static AutomationWorkflowGraphPackage LocalEventsScrapeGraph()
    {
        var trigger = NewNode("Every 6 hours", "core.scheduleTrigger", 120, 160,
            "{\"intervalMinutes\":360,\"cronExpression\":\"\"}");
        var fetch = NewNode("Fetch events page", "core.httpRequest", 400, 160,
            "{\"method\":\"GET\",\"url\":\"https://www.eventbrite.com/d/ga--warner-robins/events/\",\"headers\":{\"User-Agent\":\"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36\"},\"body\":\"\"}");
        // maxMiles 25 keeps it to things worth driving to from Kathleen; online listings are
        // dropped by default because they are tagged to a town but happen nowhere near it.
        var extract = NewNode("Extract events", "civic.extractEvents", 680, 160,
            "{\"html\":\"{{ $json.body }}\",\"includeOnline\":false,\"maxMiles\":25,\"limit\":50}");
        return new AutomationWorkflowGraphPackage(
            "Local events: scrape a calendar",
            "Fetches a public events page every 6 hours and extracts its schema.org listings. Change the URL to point at any venue, chamber or city calendar, then add a Database Row node to store what it finds.",
            [trigger, fetch, extract],
            [Connect(trigger, "main", fetch, "main"), Connect(fetch, "main", extract, "main")]);
    }

    private static AutomationWorkflowGraphPackage CrmDealWonNotifyGraph()
    {
        var trigger = NewNode("Deal changed", "database.rowChangedTrigger", 120, 160,
            "{\"wikiDatabaseId\":\"\",\"conditions\":[]}");
        var notify = NewNode("Notify owner", "core.notify", 420, 160,
            "{\"to\":\"\",\"subject\":\"Deal won\",\"message\":\"A deal just moved to Won: {{ $json }}\"}");
        return new AutomationWorkflowGraphPackage(
            "CRM: Notify on deal won",
            "Emails someone when a watched database row changes - add a Stage-equals-Won condition once the database is connected.",
            [trigger, notify],
            [Connect(trigger, "main", notify, "main")]);
    }

    private static AutomationWorkflowGraphPackage ScheduledDigestGraph()
    {
        var trigger = NewNode("Weekly schedule", "core.scheduleTrigger", 120, 160, "{\"intervalMinutes\":60,\"cronExpression\":\"0 9 * * 1\"}");
        var template = NewNode("Build message", "core.template", 420, 160, "{\"outputField\":\"digest\",\"template\":\"Weekly digest for {{ $json.scheduledAt }}.\"}");
        var notify = NewNode("Send digest", "core.notify", 720, 160, "{\"to\":\"\",\"subject\":\"Weekly digest\",\"message\":\"{{ $json.digest }}\"}");
        return new AutomationWorkflowGraphPackage(
            "Scheduled digest",
            "Builds and emails a short message every Monday morning - replace the template with a real summary.",
            [trigger, template, notify],
            [Connect(trigger, "main", template, "main"), Connect(template, "main", notify, "main")]);
    }

    private static AutomationWorkflowGraphPackage WebhookRelayGraph()
    {
        var trigger = NewNode("Incoming webhook", "core.webhookTrigger", 120, 160, "{\"path\":\"incoming-relay\"}");
        var forward = NewNode("Forward payload", "core.httpRequest", 420, 160, "{\"method\":\"POST\",\"url\":\"https://example.com/webhook\",\"headers\":{},\"body\":\"{{ $json }}\"}");
        return new AutomationWorkflowGraphPackage(
            "Webhook relay",
            "Receives a webhook call and forwards its payload to another URL.",
            [trigger, forward],
            [Connect(trigger, "main", forward, "main")]);
    }

    private static AutomationNodeView NewNode(string name, string typeKey, double x, double y, string parametersJson) => new(
        Guid.NewGuid(), name, typeKey, 1, x, y, parametersJson, null, false, false, false, 1, 0, 0, string.Empty);

    private static AutomationConnectionView Connect(AutomationNodeView source, string sourceOutput, AutomationNodeView target, string targetInput) =>
        new(Guid.NewGuid(), source.Id, sourceOutput, target.Id, targetInput);
}
