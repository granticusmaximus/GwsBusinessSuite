using FluentAssertions;
using GwsBusinessSuite.Application.Diagnostics;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Diagnostics;
using GwsBusinessSuite.Web;
using GwsBusinessSuite.Web.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace GwsBusinessSuite.Tests;

// Logging plan, Phase 1 (12-11-26): client reports, redaction, fingerprints, the issues store,
// the JSON log's trace lookup, and the Serilog wiring that ties them together.
public sealed class LoggingDiagnosticsTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private LogIssueService _store = null!;
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 16, 55, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        IDbContextFactory<ApplicationDbContext> factory = new Factory(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        _store = new LogIssueService(factory);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public void ClientReport_ShouldRejectNoiseAndUnknownOrigins_AndRedactWhatItKeeps()
    {
        ClientErrorReport.From(new ClientErrorInput("browser", "error", "  ", null, null, null)).Should().BeNull();
        ClientErrorReport.From(new ClientErrorInput("attacker", "error", "boom", null, null, null)).Should().BeNull();

        var report = ClientErrorReport.From(new ClientErrorInput(
            "Browser", "weird", "Failed for grant@example.com with Bearer abc.def.ghi", new string('x', 5000),
            "/admin/mail?token=secret#inbox", "0123456789ABCDEF0123456789abcdef"))!;

        report.Origin.Should().Be("browser");
        report.Kind.Should().Be("error", "an unknown kind falls back");
        report.Message.Should().Be("Failed for [email] with Bearer [redacted]");
        report.Stack!.Length.Should().Be(ClientErrorReport.MaximumStack + 1, "capped, with an ellipsis");
        report.Page.Should().Be("/admin/mail", "the query string can carry tokens and search text");
        report.Trace.Should().Be("0123456789abcdef0123456789abcdef");
        ClientErrorReport.From(new ClientErrorInput("mac-app", "native", "x", null, null, "not-a-trace"))!.Trace.Should().BeNull();
    }

    [Theory]
    [InlineData("https://x/cb?code=abc123&state=1", "https://x/cb?code=[redacted]&state=1")]
    [InlineData("api_key=sk-live-1 and password=hunter2", "api_key=[redacted] and password=[redacted]")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "jwt [token]")]
    [InlineData("Basic Z3JhbnQ6c2VjcmV0", "Basic [redacted]")]
    public void Redactor_ShouldRemoveSecrets(string text, string expected) => LogRedactor.Redact(text).Should().Be(expected);

    [Fact]
    public void Fingerprint_ShouldGroupBySameCause_AndSplitDifferentOnes()
    {
        var a = Event("Order {OrderId} failed after {Seconds} s", "Order 42 failed after 3 s");
        var b = Event("Order {OrderId} failed after {Seconds} s", "Order 99 failed after 7 s");
        LogFingerprint.For(a).Should().Be(LogFingerprint.For(b));
        LogFingerprint.For(a with { ExceptionType = "System.TimeoutException" }).Should().NotBe(LogFingerprint.For(a));

        // Client reports share one template: they group by the message itself, ids blanked.
        var client1 = Event("Client {ClientKind} on {ClientPage}: {ClientMessage}", "x", origin: "browser") with { ClientMessage = "Cannot read 'x' of record 12" };
        var client2 = client1 with { ClientMessage = "Cannot read 'y' of record 98" };
        var client3 = client1 with { ClientMessage = "map is not defined" };
        LogFingerprint.For(client1).Should().Be(LogFingerprint.For(client2));
        LogFingerprint.For(client1).Should().NotBe(LogFingerprint.For(client3));

        LogFingerprint.FirstAppFrame("System.Exception: x\n   at System.Net.Foo()\n   at GwsBusinessSuite.Infrastructure.Services.MailService.SendAsync(String to) in /src/Mail.cs:line 42")
            .Should().Be("GwsBusinessSuite.Infrastructure.Services.MailService.SendAsync");
    }

    [Fact]
    public async Task Store_ShouldCountSightings_KeepTheLatestFew_AndReopenWhatComesBack()
    {
        var sightings = Enumerable.Range(0, 30)
            .Select(i => Event("Feed {Name} failed", $"Feed {i} failed", at: Now.AddSeconds(i), trace: i.ToString("x32")))
            .ToList();
        await _store.RecordAsync(sightings);
        await _store.RecordAsync([Event("Something else", "Something else")]);

        var issues = await _store.ListAsync(new LogIssueQuery());
        issues.Should().HaveCount(2);
        var feed = issues.Single(issue => issue.Title.StartsWith("Feed", StringComparison.Ordinal));
        feed.Count.Should().Be(30);
        feed.LastSeen.Should().Be(Now.AddSeconds(29));

        var detail = (await _store.GetAsync(feed.Id))!;
        detail.Occurrences.Should().HaveCount(LogIssueLimits.OccurrencesPerIssue);
        detail.Occurrences[0].Message.Should().Be("Feed 29 failed", "newest first");

        await _store.SetStatusAsync(feed.Id, LogIssueStatuses.Resolved, "fixed in abc1234");
        (await _store.ListAsync(new LogIssueQuery(Status: LogIssueStatuses.Open))).Should().ContainSingle();
        await _store.RecordAsync([Event("Feed {Name} failed", "Feed 1 failed", at: Now.AddHours(1), release: "def5678")]);
        var reopened = (await _store.GetAsync(feed.Id))!.Issue;
        reopened.Status.Should().Be(LogIssueStatuses.Open);
        reopened.StatusNote.Should().Contain("def5678");

        (await _store.ListAsync(new LogIssueQuery(Search: "else"))).Should().ContainSingle();
        (await _store.PruneAsync(Now.AddDays(LogIssueLimits.RetentionDays + 1))).Should().Be(2);
        (await _store.ListAsync(new LogIssueQuery())).Should().BeEmpty();
    }

    [Fact]
    public async Task TraceReader_ShouldFindServerAndClientLinesForATrace()
    {
        var directory = Directory.CreateTempSubdirectory("gws-logs-").FullName;
        try
        {
            const string Trace = "4bf92f3577b34da6a3ce929d0e0e4736";
            var path = Path.Combine(directory, "gwssuite-20261112.clef");
            await File.WriteAllLinesAsync(path,
            [
                $$"""{"@t":"2026-11-12T16:55:01.000Z","@m":"Rendering /admin/osint","@tr":"{{Trace}}","SourceContext":"Server.Page","Origin":"server","UserName":"grant"}""",
                """{"@t":"2026-11-12T16:55:02.000Z","@m":"Unrelated","@tr":"00000000000000000000000000000000","SourceContext":"Other"}""",
                $$"""{"@t":"2026-11-12T16:55:03.000Z","@m":"Client native","@l":"Warning","ClientTraceId":"{{Trace}}","SourceContext":"GwsBusinessSuite.ClientErrors","Origin":"mac-app","ClientPage":"/admin/osint"}""",
                "not json " + Trace,
            ]);

            var lines = await new JsonLogTraceReader(directory).ReadTraceAsync(Trace.ToUpperInvariant());

            lines.Select(line => line.Origin).Should().Equal("server", "mac-app");
            lines[1].Level.Should().Be("Warning");
            lines[1].Page.Should().Be("/admin/osint");
            (await new JsonLogTraceReader(directory).ReadTraceAsync("../../etc/passwd")).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Serilog_ShouldLetAClientsOriginWin_AndQueueOnlyWarnings()
    {
        var sink = new LogIssueSink();
        var json = new StringWriter();
        using var logger = new Serilog.LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Origin", "server")
            .Enrich.WithProperty("Release", "abc1234")
            .WriteTo.Sink(new JsonSink(json))
            .WriteTo.Sink(sink, LogEventLevel.Warning)
            .CreateLogger();
        var factory = new Serilog.Extensions.Logging.SerilogLoggerFactory(logger);

        factory.CreateLogger("Quiet").LogInformation("Just information");
        var report = ClientErrorReport.From(new ClientErrorInput("mac-app", "native", "Overwatch map: click on a pin that opens nothing", "at x", "/admin/osint",
            "4bf92f3577b34da6a3ce929d0e0e4736"))!;
        // As live: logged inside the POST that carried it, which has its own trace and path.
        using (new System.Diagnostics.Activity("POST /api/diagnostics/client").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start())
        using (Serilog.Context.LogContext.PushProperty("RequestPath", "/api/diagnostics/client"))
        {
            DiagnosticsEndpointExtensions.Log(factory.CreateLogger(DiagnosticsEndpointExtensions.ClientErrorCategory), report, "GWS/1.0");
        }
        using (LogIssueSink.Suppress())
        {
            factory.CreateLogger("Store").LogWarning("While saving");
        }

        sink.Reader.TryRead(out var captured).Should().BeTrue();
        captured!.Origin.Should().Be("mac-app");
        captured.Release.Should().Be("abc1234");
        captured.Page.Should().Be("/admin/osint", "the page it came from, not /api/diagnostics/client");
        captured.TraceId.Should().Be("4bf92f3577b34da6a3ce929d0e0e4736", "the page's trace, not the reporting request's");
        captured.ClientMessage.Should().Be("Overwatch map: click on a pin that opens nothing");
        sink.Reader.TryRead(out _).Should().BeFalse("information isn't an issue, and the store's own lines are suppressed");
        json.ToString().Should().Contain("\"Origin\":\"mac-app\"").And.Contain("\"Origin\":\"server\"");
    }

    [Fact]
    public void Bundle_ShouldCarryTheEvidence_Redacted()
    {
        var issue = new LogIssueSummary(Guid.NewGuid(), "Mail to grant@example.com failed", "Error", "server", "MailService", "System.TimeoutException",
            3, Now.AddHours(-1), Now, "abc1234", "abc1234", "open", null);
        var detail = new LogIssueDetail(issue, "at GwsBusinessSuite.Mail.Send()", [new LogOccurrenceView(Now, "Mail failed", "4bf92f3577b34da6a3ce929d0e0e4736", "grant", "/admin/mail", "abcd1234", "abc1234")]);
        var markdown = LogIssueBundle.ToMarkdown(detail, [new LogLine(Now, "Error", "server", "MailService", "token=xyz", null, "grant", "/admin/mail")], "abc1234");

        markdown.Should().Contain("# Mail to [email] failed").And.Contain("System.TimeoutException").And.Contain("12-11-26 16:55 UTC")
            .And.Contain("token=[redacted]").And.Contain("## Trace of the latest occurrence").And.NotContain("grant@example.com");
    }

    [Fact]
    public void JsonLog_ShouldSitBesideTheTextLog() =>
        SerilogBootstrapper.JsonLogPath("/app/data/logs/gwssuite-.log").Should().Be("/app/data/logs/gwssuite-.clef");

    // Logging Phase 2 (13-11-26): known causes close by rule; quiet issues close themselves.
    [Fact]
    public async Task AKnownCause_ShouldBeMarkedExpected_WithTheReason()
    {
        await _store.RecordAsync([Event("Mail failed", "SMTP connect to smtp.gmail.com:587 timed out")]);
        var issue = (await _store.ListAsync(new LogIssueQuery())).Single();
        issue.Status.Should().Be(GwsBusinessSuite.Domain.Entities.LogIssueStatuses.Expected);
        issue.StatusNote.Should().Contain("DigitalOcean");
    }

    [Fact]
    public async Task AnOpenIssueQuietForAWeek_ShouldCloseItself_AndReopenIfItComesBack()
    {
        await _store.RecordAsync([Event("Feed {Name} failed", "Feed 1 failed")]);
        await _store.PruneAsync(Now.AddDays(LogIssueService.QuietDaysToClose + 1));
        (await _store.ListAsync(new LogIssueQuery())).Single().Status.Should().Be(GwsBusinessSuite.Domain.Entities.LogIssueStatuses.Resolved);

        await _store.RecordAsync([Event("Feed {Name} failed", "Feed 2 failed", at: Now.AddDays(9))]);
        (await _store.ListAsync(new LogIssueQuery())).Single().Status.Should().Be(GwsBusinessSuite.Domain.Entities.LogIssueStatuses.Open);
    }

    [Theory]
    [InlineData("GwsBusinessSuite.Application.Abstractions.ServerAiUnavailableException", "x", "AI model")]
    [InlineData(null, "GwsBusinessSuite.Infrastructure.Services.RefusedHostPauseHandler", "refused")]
    [InlineData(null, "x", null)]
    public void KnownCauses_ShouldNameTheReason(string? exceptionType, string source, string? expected)
    {
        var reason = GwsBusinessSuite.Application.Diagnostics.KnownCauses.Match(exceptionType, source, "t", null);
        if (expected is null) reason.Should().BeNull();
        else reason.Should().Contain(expected);
    }

    private static CapturedLogEvent Event(string template, string message, string origin = "server", DateTimeOffset? at = null, string? trace = null, string release = "abc1234") =>
        new(at ?? Now, "Warning", origin, "GwsBusinessSuite.Test", template, message, null, null, trace, "grant", "/admin", null, release);

    private sealed class JsonSink(TextWriter writer) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => new RenderedCompactJsonFormatter().Format(logEvent, writer);
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }
}
