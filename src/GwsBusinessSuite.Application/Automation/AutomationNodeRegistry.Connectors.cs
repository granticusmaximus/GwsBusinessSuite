using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GwsBusinessSuite.Application.Automation;

// Slack/Gmail/Calendar node executors - split into its own partial-class file since
// AutomationNodeRegistry.cs is already large. Each node pulls its credential's decrypted
// accessToken (an "oauth2"-type AutomationCredential minted by SlackOAuthService/
// GoogleOAuthService - see AutomationConnectorOAuthModels.cs) and calls the provider's REST
// API directly through the same SSRF-protected IAutomationHttpClient every other node uses -
// no provider SDK, matching core.httpRequest's own approach.
public sealed partial class AutomationNodeRegistry
{
    private async Task<AutomationNodeRunResult> ExecuteSlackSendMessageAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? credentialJson,
        IReadOnlyDictionary<string, JsonElement>? nodeOutputsByName,
        CancellationToken cancellationToken)
    {
        var accessToken = RequireOAuthAccessToken(credentialJson, node.Name, "Slack");
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var channel = ResolveText(parameters["channel"]?.GetValue<string>() ?? string.Empty, input, nodeOutputsByName);
        var text = ResolveText(parameters["text"]?.GetValue<string>() ?? string.Empty, input, nodeOutputsByName);
        if (string.IsNullOrWhiteSpace(channel)) throw new InvalidOperationException($"{node.Name} requires a channel.");
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException($"{node.Name} requires message text.");

        var response = await httpClient.SendAsync(new AutomationHttpRequest(
            HttpMethod.Post,
            "https://slack.com/api/chat.postMessage",
            JsonSerializer.Serialize(new { channel, text }),
            BearerHeaders(accessToken)), cancellationToken);

        var responseNode = ParseJsonOrWrap(response.Body);
        var ok = responseNode["ok"] is JsonValue okValue && okValue.TryGetValue<bool>(out var okBool) && okBool;
        if (response.StatusCode is < 200 or >= 300 || !ok)
        {
            var error = responseNode["error"]?.GetValue<string>() ?? "unknown_error";
            throw new InvalidOperationException($"{node.Name} failed: Slack returned '{error}'.");
        }

        var output = new JsonObject
        {
            ["ok"] = true,
            ["channel"] = responseNode["channel"]?.DeepClone(),
            ["ts"] = responseNode["ts"]?.DeepClone()
        };
        return ToNodeResult(output, accessToken);
    }

    private async Task<AutomationNodeRunResult> ExecuteGmailSendEmailAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? credentialJson,
        IReadOnlyDictionary<string, JsonElement>? nodeOutputsByName,
        CancellationToken cancellationToken)
    {
        var accessToken = RequireOAuthAccessToken(credentialJson, node.Name, "Google");
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var to = ResolveText(parameters["to"]?.GetValue<string>() ?? string.Empty, input, nodeOutputsByName);
        var subject = ResolveText(parameters["subject"]?.GetValue<string>() ?? string.Empty, input, nodeOutputsByName);
        var body = ResolveText(parameters["body"]?.GetValue<string>() ?? string.Empty, input, nodeOutputsByName);
        if (string.IsNullOrWhiteSpace(to)) throw new InvalidOperationException($"{node.Name} requires a recipient.");

        // Plain ASCII subject/body only - no RFC 2047 encoding for non-ASCII subjects. Good
        // enough for the workflow-notification use case this node targets; a mailbox-grade
        // composer belongs in a dedicated email-campaign feature, not an automation node.
        var raw = $"To: {to}\r\nSubject: {subject}\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\n{body}";
        var rawBase64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var response = await httpClient.SendAsync(new AutomationHttpRequest(
            HttpMethod.Post,
            "https://gmail.googleapis.com/gmail/v1/users/me/messages/send",
            JsonSerializer.Serialize(new { raw = rawBase64Url }),
            BearerHeaders(accessToken)), cancellationToken);
        if (response.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"{node.Name} failed: Gmail returned status {response.StatusCode}.");

        var responseNode = ParseJsonOrWrap(response.Body);
        var output = new JsonObject
        {
            ["id"] = responseNode["id"]?.DeepClone(),
            ["threadId"] = responseNode["threadId"]?.DeepClone()
        };
        return ToNodeResult(output, accessToken);
    }

    private async Task<AutomationNodeRunResult> ExecuteCalendarCreateEventAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? credentialJson,
        IReadOnlyDictionary<string, JsonElement>? nodeOutputsByName,
        CancellationToken cancellationToken)
    {
        var accessToken = RequireOAuthAccessToken(credentialJson, node.Name, "Google");
        var parameters = ParseObject(node.ParametersJson, node.Name);
        var calendarIdRaw = ResolveText(parameters["calendarId"]?.GetValue<string>() ?? "primary", input, nodeOutputsByName);
        var calendarId = string.IsNullOrWhiteSpace(calendarIdRaw) ? "primary" : calendarIdRaw;
        var summary = ResolveText(parameters["summary"]?.GetValue<string>() ?? string.Empty, input, nodeOutputsByName);
        var description = ResolveText(parameters["description"]?.GetValue<string>() ?? string.Empty, input, nodeOutputsByName);
        var startsAtText = ResolveText(parameters["startsAt"]?.GetValue<string>() ?? string.Empty, input, nodeOutputsByName);
        var durationMinutes = parameters["durationMinutes"]?.GetValue<int>() ?? 30;

        if (string.IsNullOrWhiteSpace(summary)) throw new InvalidOperationException($"{node.Name} requires a summary.");
        if (!DateTimeOffset.TryParse(startsAtText, out var startsAt))
            throw new InvalidOperationException($"{node.Name} requires a valid startsAt date/time.");
        var endsAt = startsAt.AddMinutes(Math.Max(5, durationMinutes));

        var url = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events";
        var response = await httpClient.SendAsync(new AutomationHttpRequest(
            HttpMethod.Post,
            url,
            JsonSerializer.Serialize(new
            {
                summary,
                description,
                start = new { dateTime = startsAt.ToString("O") },
                end = new { dateTime = endsAt.ToString("O") }
            }),
            BearerHeaders(accessToken)), cancellationToken);
        if (response.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"{node.Name} failed: Google Calendar returned status {response.StatusCode}.");

        var responseNode = ParseJsonOrWrap(response.Body);
        var output = new JsonObject
        {
            ["id"] = responseNode["id"]?.DeepClone(),
            ["htmlLink"] = responseNode["htmlLink"]?.DeepClone()
        };
        return ToNodeResult(output, accessToken);
    }

    private static Dictionary<string, string> BearerHeaders(string accessToken) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {accessToken}",
            ["Content-Type"] = "application/json; charset=utf-8"
        };

    // Node evidence (AutomationNodeExecution.OutputJson) is stored unencrypted and visible in
    // the execution history UI - the bearer token must never end up there even though it's
    // never itself part of a provider's response body, matching core.httpRequest's own
    // credentialSecretValues redaction for the same reason.
    private static AutomationNodeRunResult ToNodeResult(JsonObject output, string accessToken)
    {
        var cloned = JsonSerializer.SerializeToElement(output).Clone();
        var displayJson = RedactSecrets(cloned.GetRawText(), [accessToken]);
        return new AutomationNodeRunResult(
            new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.OrdinalIgnoreCase) { ["main"] = [cloned] },
            displayJson);
    }

    private static string RequireOAuthAccessToken(string? credentialJson, string nodeName, string providerLabel)
    {
        if (string.IsNullOrWhiteSpace(credentialJson))
            throw new InvalidOperationException($"{nodeName} requires a connected {providerLabel} OAuth credential - select one on this node.");

        var data = ParseObject(credentialJson, "Credential");
        var accessToken = data["accessToken"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException($"{nodeName}'s credential has no stored access token - reconnect {providerLabel} from Automation Credentials.");
        return accessToken;
    }

    private static JsonObject ParseJsonOrWrap(string body)
    {
        try { return JsonNode.Parse(body) as JsonObject ?? []; }
        catch (JsonException) { return new JsonObject { ["raw"] = body }; }
    }
}
