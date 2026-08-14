using System.Text;
using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Automation;

namespace GwsBusinessSuite.Tests;

public sealed class AutomationConnectorNodesTests
{
    private static readonly JsonElement EmptyInput = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task SlackSendMessage_ShouldPostWithBearerAuth_AndReturnChannelAndTimestamp()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, """{"ok":true,"channel":"C123","ts":"1699999999.000100"}""", new Dictionary<string, string>()));
        var registry = new AutomationNodeRegistry(httpClient);
        var node = NewNode("slack.sendMessage", """{"channel":"#general","text":"Deploy finished"}""");
        var credentialJson = """{"accessToken":"xoxb-secret-token"}""";

        var result = await registry.ExecuteAsync(node, EmptyInput, credentialJson);

        httpClient.Requests.Should().ContainSingle();
        var request = httpClient.Requests[0];
        request.Url.Should().Be("https://slack.com/api/chat.postMessage");
        request.Headers["Authorization"].Should().Be("Bearer xoxb-secret-token");
        request.Body.Should().Contain("\"channel\":\"#general\"").And.Contain("Deploy finished");
        result.Outputs["main"][0].GetProperty("channel").GetString().Should().Be("C123");
    }

    [Fact]
    public async Task SlackSendMessage_ShouldThrow_WhenSlackReturnsOkFalse()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, """{"ok":false,"error":"channel_not_found"}""", new Dictionary<string, string>()));
        var registry = new AutomationNodeRegistry(httpClient);
        var node = NewNode("slack.sendMessage", """{"channel":"#nope","text":"hi"}""");

        var act = () => registry.ExecuteAsync(node, EmptyInput, """{"accessToken":"token"}""");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*channel_not_found*");
    }

    [Fact]
    public async Task SlackSendMessage_ShouldThrow_WhenNoCredentialIsAttached()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>()));
        var registry = new AutomationNodeRegistry(httpClient);
        var node = NewNode("slack.sendMessage", """{"channel":"#general","text":"hi"}""");

        var act = () => registry.ExecuteAsync(node, EmptyInput, credentialJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        httpClient.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GmailSendEmail_ShouldBase64UrlEncodeARfc2822Message()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, """{"id":"18abc","threadId":"18abc"}""", new Dictionary<string, string>()));
        var registry = new AutomationNodeRegistry(httpClient);
        var node = NewNode("gmail.sendEmail", """{"to":"jamie@example.test","subject":"Weekly report","body":"See attached."}""");

        var result = await registry.ExecuteAsync(node, EmptyInput, """{"accessToken":"ya29.token"}""");

        var request = httpClient.Requests.Should().ContainSingle().Subject;
        request.Url.Should().Be("https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
        request.Headers["Authorization"].Should().Be("Bearer ya29.token");
        using var sentBody = JsonDocument.Parse(request.Body!);
        var raw = sentBody.RootElement.GetProperty("raw").GetString()!;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/').PadRight(raw.Length + (4 - raw.Length % 4) % 4, '=')));
        decoded.Should().Contain("To: jamie@example.test").And.Contain("Subject: Weekly report").And.Contain("See attached.");
        result.Outputs["main"][0].GetProperty("id").GetString().Should().Be("18abc");
    }

    [Fact]
    public async Task GmailSendEmail_ShouldThrow_WhenNoRecipientIsGiven()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>()));
        var registry = new AutomationNodeRegistry(httpClient);
        var node = NewNode("gmail.sendEmail", """{"to":"","subject":"x","body":"y"}""");

        var act = () => registry.ExecuteAsync(node, EmptyInput, """{"accessToken":"token"}""");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CalendarCreateEvent_ShouldSendStartAndEndDerivedFromDuration()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, """{"id":"evt1","htmlLink":"https://calendar.google.com/evt1"}""", new Dictionary<string, string>()));
        var registry = new AutomationNodeRegistry(httpClient);
        var node = NewNode("calendar.createEvent", """{"calendarId":"primary","summary":"Kickoff","description":"","startsAt":"2026-09-01T15:00:00Z","durationMinutes":45}""");

        var result = await registry.ExecuteAsync(node, EmptyInput, """{"accessToken":"ya29.token"}""");

        var request = httpClient.Requests.Should().ContainSingle().Subject;
        request.Url.Should().Be("https://www.googleapis.com/calendar/v3/calendars/primary/events");
        using var sentBody = JsonDocument.Parse(request.Body!);
        sentBody.RootElement.GetProperty("start").GetProperty("dateTime").GetString().Should().StartWith("2026-09-01T15:00:00");
        sentBody.RootElement.GetProperty("end").GetProperty("dateTime").GetString().Should().StartWith("2026-09-01T15:45:00");
        result.Outputs["main"][0].GetProperty("id").GetString().Should().Be("evt1");
    }

    [Fact]
    public async Task CalendarCreateEvent_ShouldThrow_WhenStartsAtIsNotAValidDate()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>()));
        var registry = new AutomationNodeRegistry(httpClient);
        var node = NewNode("calendar.createEvent", """{"summary":"Kickoff","startsAt":"not-a-date","durationMinutes":30}""");

        var act = () => registry.ExecuteAsync(node, EmptyInput, """{"accessToken":"token"}""");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ConnectorNodes_ShouldRedactTheAccessTokenFromDisplayOutput_IfItLeaksIntoTheResponse()
    {
        // Simulates a misbehaving/echo endpoint reflecting the bearer token back - the
        // persisted execution-history JSON must never contain it, mirroring core.httpRequest's
        // own credentialSecretValues redaction.
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, """{"ok":true,"channel":"xoxb-secret-token","ts":"1"}""", new Dictionary<string, string>()));
        var registry = new AutomationNodeRegistry(httpClient);
        var node = NewNode("slack.sendMessage", """{"channel":"#general","text":"hi"}""");

        var result = await registry.ExecuteAsync(node, EmptyInput, """{"accessToken":"xoxb-secret-token"}""");

        result.DisplayOutputJson.Should().NotContain("xoxb-secret-token").And.Contain("[redacted]");
    }

    private static AutomationNodeSnapshot NewNode(string typeKey, string parametersJson) => new(
        Guid.NewGuid(), typeKey, typeKey, 1, parametersJson, null, false, false, false, 1, 0, 0);

    private sealed class RecordingHttpClient(AutomationHttpResponse response) : IAutomationHttpClient
    {
        public List<AutomationHttpRequest> Requests { get; } = [];

        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }
}
