using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using GwsBusinessSuite.Application.ThreatIntel;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Tests;

public sealed class ThreatExposureTests
{
    // ---- Indicator classification ---------------------------------------------------------

    [Theory]
    [InlineData("8.8.8.8", IndicatorKind.IpAddress, "8.8.8.8")]
    [InlineData("2606:4700::1111", IndicatorKind.IpAddress, "2606:4700::1111")]
    [InlineData("Example.COM.", IndicatorKind.Domain, "example.com")]
    [InlineData("evil[.]example[.]net", IndicatorKind.Domain, "evil.example.net")]
    [InlineData("hxxps://bad.example.org/x.exe", IndicatorKind.Url, "https://bad.example.org/x.exe")]
    [InlineData("cve-2024-3400", IndicatorKind.Cve, "CVE-2024-3400")]
    [InlineData("D41D8CD98F00B204E9800998ECF8427E", IndicatorKind.Md5, "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("da39a3ee5e6b4b0d3255bfef95601890afd80709", IndicatorKind.Sha1, "da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", IndicatorKind.Sha256, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("not an indicator", IndicatorKind.Unknown, "not an indicator")]
    [InlineData("localhost", IndicatorKind.Unknown, "localhost")]
    public void Classify_ShouldRecognizeEachIndicatorType(string input, IndicatorKind kind, string value)
    {
        var result = ThreatIndicatorClassifier.Classify(input);
        result.Kind.Should().Be(kind);
        result.Value.Should().Be(value);
    }

    [Fact]
    public void Classify_ShouldExposeTheHostOfAUrl()
    {
        ThreatIndicatorClassifier.Classify("http://Bad.Example.org:8080/a").Host.Should().Be("bad.example.org");
    }

    // ---- DNS over HTTPS --------------------------------------------------------------------

    [Fact]
    public void DohAnswers_ShouldJoinSplitTxtStrings_AndKeepOnlyTheAskedType()
    {
        const string json = """
            {"Status":0,"Answer":[
              {"name":"www.example.com","type":5,"data":"example.com."},
              {"name":"example.com","type":16,"data":"\"v=spf1 include:_spf.goo\" \"gle.com ~all\""},
              {"name":"example.com","type":16,"data":"\"google-site-verification=abc\""}]}
            """;

        DnsOverHttpsLookup.ParseAnswers(json, 16).Should().Equal("v=spf1 include:_spf.google.com ~all", "google-site-verification=abc");
    }

    [Fact]
    public void DohAnswers_ShouldTreatNxdomainAsNoRecords_AndOtherFailuresAsErrors()
    {
        DnsOverHttpsLookup.ParseAnswers("""{"Status":3}""", 15).Should().BeEmpty();
        var servfail = () => DnsOverHttpsLookup.ParseAnswers("""{"Status":2}""", 15);
        servfail.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public void RdapBootstrap_ShouldPreferHttpsAndNormalizeTheTrailingSlash()
    {
        var services = DomainIntelService.ParseBootstrap(JsonNode.Parse(
            """{"services":[[["com","net"],["http://rdap.verisign.com/com/v1/","https://rdap.verisign.com/com/v1"]],[["kg"],["http://rdap.cctld.kg"]]]}"""));

        services[0].Keys.Should().Equal("com", "net");
        services[0].BaseUrl.Should().Be("https://rdap.verisign.com/com/v1/");
        services[1].BaseUrl.Should().Be("http://rdap.cctld.kg/");
    }

    // ---- Email security --------------------------------------------------------------------

    private static EmailSecurityEvaluator.Inputs Mail(
        string[]? mx = null, string[]? txt = null, string[]? dmarc = null, string[]? dkim = null,
        string[]? sts = null, string? policy = null, string[]? tlsRpt = null) =>
        new("example.com", mx ?? ["mx.example.com"], txt ?? [], dmarc ?? [], dkim ?? [], sts ?? [], policy, tlsRpt ?? []);

    [Fact]
    public void Email_ShouldFlagDmarcNone_AsTheGwsappNetSetupDoes()
    {
        // gwsapp.net on 2026-10-05: Google SPF ~all, DKIM on the "google" selector, DMARC p=none.
        var report = EmailSecurityEvaluator.Evaluate(Mail(
            txt: ["google-site-verification=x", "v=spf1 include:_spf.google.com ~all"],
            dmarc: ["v=DMARC1; p=none;"],
            dkim: ["google"]), []);

        report.SpfRecord.Should().Be("v=spf1 include:_spf.google.com ~all");
        report.DmarcPolicy.Should().Be("none");
        report.Issues.Select(i => i.Code).Should().Contain(["dmarc-none", "dmarc-no-reports", "mta-sts-missing", "tls-rpt-missing"]);
        report.Issues.Should().NotContain(i => i.Code.StartsWith("spf") || i.Code == "dkim-not-found");
        report.Grade.Should().Be("C");
    }

    [Fact]
    public void Email_ShouldFailADomainThatAnyoneCanSpoof()
    {
        var report = EmailSecurityEvaluator.Evaluate(Mail(txt: ["v=spf1 +all"]), []);

        report.Issues.Should().Contain(i => i.Code == "spf-pass-all" && i.Severity == ThreatFindingSeverities.Critical);
        report.Issues.Should().Contain(i => i.Code == "dmarc-missing");
        report.Grade.Should().Be("F");
    }

    [Fact]
    public void Email_ShouldCatchMultipleSpfRecords_AndAStrictSetupShouldScoreA()
    {
        EmailSecurityEvaluator.Evaluate(Mail(txt: ["v=spf1 -all", "v=spf1 include:x -all"]), [])
            .Issues.Should().Contain(i => i.Code == "spf-multiple");

        var strict = EmailSecurityEvaluator.Evaluate(Mail(
            txt: ["v=spf1 include:_spf.google.com -all"],
            dmarc: ["v=DMARC1; p=reject; rua=mailto:d@example.com"],
            dkim: ["google"],
            sts: ["v=STSv1; id=1"],
            policy: "version: STSv1\nmode: enforce\nmx: mx.example.com\nmax_age: 86400",
            tlsRpt: ["v=TLSRPTv1; rua=mailto:t@example.com"]), []);
        strict.Issues.Should().BeEmpty();
        strict.MtaStsMode.Should().Be("enforce");
        strict.Grade.Should().Be("A");
    }

    [Fact]
    public void Email_ForADomainWithoutMx_ShouldOnlyAskForALockedDownSpf()
    {
        var report = EmailSecurityEvaluator.Evaluate(Mail(mx: []), []);

        report.Issues.Should().Contain(i => i.Code == "spf-missing" && i.Severity == ThreatFindingSeverities.Medium);
        report.Issues.Select(i => i.Code).Should().NotContain(["dkim-not-found", "mta-sts-missing", "tls-rpt-missing"]);
    }

    // ---- Dependencies ---------------------------------------------------------------------

    [Fact]
    public void DepsJson_ShouldListOnlyPackages()
    {
        const string json = """
            {"libraries":{
              "GwsBusinessSuite.Web/1.0.0":{"type":"project"},
              "MailKit/4.18.1":{"type":"package","serviceable":true},
              "Markdig/1.4.0":{"type":"package"},
              "Microsoft.AspNetCore.App/10.0.0":{"type":"runtimepack"}}}
            """;

        RuntimeDependencyInventory.ParseDepsJson(json).Should().Equal(
            new DependencyPackage("NuGet", "MailKit", "4.18.1"),
            new DependencyPackage("NuGet", "Markdig", "1.4.0"));
    }

    // The browser libraries can't be read from the build, so this keeps the hand-kept list honest:
    // every pinned jsDelivr npm package referenced in src/ must be in FrontEndPackages.
    [Fact]
    public void FrontEndPackages_ShouldCoverEveryJsDelivrReference()
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));
        var referenced = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor") || f.EndsWith(".cs") || f.EndsWith(".js") || f.EndsWith(".html"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"wwwroot{Path.DirectorySeparatorChar}lib"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"cdn\.jsdelivr\.net/npm/((?:@[\w.-]+/)?[\w.-]+)@(\d[\w.-]*)").Select(m => (Name: m.Groups[1].Value, Version: m.Groups[2].Value)))
            .Distinct()
            .ToList();

        referenced.Should().NotBeEmpty();
        foreach (var (name, version) in referenced)
        {
            RuntimeDependencyInventory.FrontEndPackages.Should().Contain(new DependencyPackage("npm", name, version),
                $"src/ loads {name}@{version} from jsDelivr");
        }
    }

    [Fact]
    public void OsvAdvisory_ShouldReadSeverityFixedVersionAndAliases()
    {
        var detail = JsonNode.Parse("""
            {"id":"GHSA-5crp-9r3c-p9vr","summary":"Improper Handling of Exceptional Conditions in Newtonsoft.Json","aliases":["CVE-2024-21907"],
             "affected":[{"package":{"name":"Newtonsoft.Json","ecosystem":"NuGet"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"13.0.1"}]}]}],
             "database_specific":{"severity":"HIGH"}}
            """);

        var advisory = DependencyAdvisoryService.ToAdvisory(new DependencyPackage("NuGet", "Newtonsoft.Json", "12.0.1"), "GHSA-5crp-9r3c-p9vr", detail);

        advisory.Severity.Should().Be("high");
        advisory.FixedVersion.Should().Be("13.0.1");
        advisory.Aliases.Should().Equal("CVE-2024-21907");
        advisory.ReferenceUrl.Should().Be("https://github.com/advisories/GHSA-5crp-9r3c-p9vr");
    }

    // ---- Monitor ---------------------------------------------------------------------------

    [Fact]
    public async Task AddAsset_ShouldNormalizeAndRejectPrivateOrDuplicateAddresses()
    {
        await using var fixture = await MonitorFixture.CreateAsync();

        var asset = await fixture.Service.AddAssetAsync("https://WWW.Example.com/path", "site", "grant");
        asset.Value.Should().Be("www.example.com");
        asset.Kind.Should().Be(ThreatAssetKinds.Domain);

        await fixture.Invoking(f => f.Service.AddAssetAsync("www.example.com", null, "grant")).Should().ThrowAsync<ArgumentException>();
        await fixture.Invoking(f => f.Service.AddAssetAsync("192.168.1.10", null, "grant")).Should().ThrowAsync<ArgumentException>();
        await fixture.Invoking(f => f.Service.AddAssetAsync("not a host", null, "grant")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AssetCheck_ShouldRaiseStateFindings_ResolveThemWhenFixed_AndNotRepeatEvents()
    {
        await using var fixture = await MonitorFixture.CreateAsync();
        await fixture.Service.AddAssetAsync("example.com", null, "grant");

        // First run: certificate expiring in 5 days and a feed listing. CT hostnames form the
        // baseline, so they don't raise "new certificate" events yet.
        fixture.CertificateExpires = fixture.Now.AddDays(5);
        fixture.FeedMatches = [new IndicatorMatch("URLhaus", "http://example.com/x.exe", "malware_download, online", fixture.Now, "https://urlhaus.abuse.ch/url/1/")];
        fixture.Hostnames = ["example.com", "www.example.com"];
        var first = await fixture.Service.RunChecksAsync(ThreatCheckScope.Assets);

        var open = await fixture.Service.ListFindingsAsync();
        open.Select(f => f.Title).Should().Contain(t => t.Contains("expires in 5 days")).And.Contain(t => t.Contains("listed on URLhaus"));
        open.Should().NotContain(f => f.Title.StartsWith("New certificate"));
        first.NewFindings.Should().Be(open.Count);

        // Second run: certificate renewed, feed listing gone, one new hostname appears.
        fixture.CertificateExpires = fixture.Now.AddDays(90);
        fixture.FeedMatches = [];
        fixture.Hostnames = ["example.com", "www.example.com", "admin.example.com"];
        var second = await fixture.Service.RunChecksAsync(ThreatCheckScope.Assets);

        second.ResolvedFindings.Should().Be(2);
        open = await fixture.Service.ListFindingsAsync();
        open.Should().ContainSingle(f => f.Title == "New certificate issued for admin.example.com");

        // Third run, same state: the event isn't raised twice and doesn't auto-resolve.
        var third = await fixture.Service.RunChecksAsync(ThreatCheckScope.Assets);
        third.NewFindings.Should().Be(0);
        third.ResolvedFindings.Should().Be(0);

        // Acknowledging an event closes it.
        await fixture.Service.AcknowledgeFindingAsync(open.Single(f => f.Title.StartsWith("New certificate")).Id, "grant");
        (await fixture.Service.ListFindingsAsync()).Should().NotContain(f => f.Title.StartsWith("New certificate"));
    }

    [Fact]
    public async Task StackCheck_ShouldMatchRecentKevEntriesByVendorAndProduct()
    {
        await using var fixture = await MonitorFixture.CreateAsync();
        await fixture.Service.AddStackItemAsync("PAN-OS firewall", "Palo Alto", "PAN-OS", "pan-os", "grant");
        fixture.Kev =
        [
            new("CVE-2026-1111", "Palo Alto Networks", "PAN-OS", "Recent injection", "", "Patch.", DateOnly.FromDateTime(fixture.Now.UtcDateTime.AddDays(-3)), null, true),
            new("CVE-2019-0001", "Palo Alto Networks", "PAN-OS", "Ancient", "", "Patch.", new DateOnly(2019, 1, 1), null, false),
            new("CVE-2026-2222", "Ivanti", "Connect Secure", "Other vendor", "", "Patch.", DateOnly.FromDateTime(fixture.Now.UtcDateTime), null, false),
        ];

        await fixture.Service.RunChecksAsync(ThreatCheckScope.Stack);

        var findings = await fixture.Service.ListFindingsAsync();
        findings.Should().ContainSingle();
        findings[0].Title.Should().Contain("CVE-2026-1111");
        findings[0].Severity.Should().Be(ThreatFindingSeverities.Critical, "it's used by ransomware");
        (await fixture.Service.ListStackAsync()).Single().OpenFindings.Should().Be(1);
    }

    [Fact]
    public async Task Digest_ShouldEmailNewFindingsOnce()
    {
        await using var fixture = await MonitorFixture.CreateAsync();
        await fixture.Service.SaveSettingsAsync(true, "alerts@example.com", 7);
        await fixture.Service.AddAssetAsync("example.com", null, "grant");
        fixture.CertificateExpires = fixture.Now.AddDays(-1);
        await fixture.Service.RunChecksAsync(ThreatCheckScope.Assets);

        var sent = await fixture.Service.SendDigestAsync(force: false);
        sent.Sent.Should().BeTrue();
        fixture.Mail.Sent.Should().ContainSingle();
        fixture.Mail.Sent[0].Subject.Should().Contain("1 new finding").And.Contain("critical");
        fixture.Mail.Sent[0].TextBody.Should().Contain("has expired");

        var again = await fixture.Service.SendDigestAsync(force: false);
        again.Sent.Should().BeFalse("nothing new since the last digest");
        fixture.Mail.Sent.Should().ContainSingle();
    }

    [Theory]
    [InlineData(true, 7, null, 8, true)]
    [InlineData(true, 7, null, 6, false)]
    [InlineData(false, 7, null, 8, false)]
    [InlineData(true, 7, 0, 9, false)]   // already sent today
    [InlineData(true, 7, -1, 9, true)]   // last sent yesterday
    public void DigestDue_ShouldSendOnceADayAfterTheChosenHour(bool enabled, int hour, int? lastSentDaysAgo, int nowHour, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 6, nowHour, 0, 0, TimeSpan.FromHours(-4));
        DateTimeOffset? lastSent = lastSentDaysAgo is { } days
            ? new DateTimeOffset(2026, 10, 6, 7, 30, 0, now.Offset).AddDays(days)
            : null;
        ThreatMonitorBackgroundService.DigestDue(enabled, hour, lastSent, now).Should().Be(expected);
    }

    [Fact]
    public void DueChecks_ShouldFollowEachInterval()
    {
        var now = DateTimeOffset.UtcNow;
        ThreatMonitorBackgroundService.DueChecks(null, null, null, now).Should().Be(ThreatCheckScope.All);
        ThreatMonitorBackgroundService.DueChecks(now.AddHours(-13), now.AddHours(-1), now.AddHours(-1), now).Should().Be(ThreatCheckScope.Assets);
        ThreatMonitorBackgroundService.DueChecks(now.AddHours(-1), now.AddHours(-25), now.AddHours(-1), now).Should().Be(ThreatCheckScope.Stack);
    }

    // ---- Investigation history -------------------------------------------------------------

    [Fact]
    public async Task Investigation_ShouldRoundTrip_AndExportToSentinelDefanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var wiki = new FakeWikiService();
        var service = new ThreatIntelWorkspaceService(db, wiki.Service, TimeProvider.System);
        var snapshot = new ThreatInvestigationSnapshot(null, null, null,
            [new IndicatorMatch("URLhaus", "http://bad.example.org/x.exe", "malware_download, online", null, "https://urlhaus.abuse.ch/url/1/")]);

        var saved = await service.SaveInvestigationAsync("bad.example.org", IndicatorKind.Domain, "In threat feeds: URLhaus.", snapshot, "grant");
        (await service.ListInvestigationsAsync()).Should().ContainSingle(i => i.Query == "bad.example.org");
        (await service.GetInvestigationSnapshotAsync(saved.Id))!.Matches.Should().ContainSingle(m => m.Feed == "URLhaus");

        var pageId = await service.ExportInvestigationToSentinelAsync(saved.Id, "grant");
        wiki.Saved.Should().ContainSingle();
        wiki.Saved[0].Title.Should().Be($"Investigation: bad[.]example[.]org ({saved.CreatedAt:yyyy-MM-dd})");
        wiki.Saved[0].BlocksJson.Should().Contain("hxxp://bad[.]example[.]org/x[.]exe").And.NotContain("http://bad.example.org");
        (await service.ListInvestigationsAsync()).Single().ExportedWikiPageId.Should().Be(pageId);
    }

    [Fact]
    public async Task KevVisit_ShouldReturnThePreviousVisit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var service = new ThreatIntelWorkspaceService(db, new FakeWikiService().Service, TimeProvider.System);

        (await service.TouchKevVisitAsync("grant")).Should().BeNull();
        (await service.TouchKevVisitAsync("grant")).Should().NotBeNull();
        (await service.TouchKevVisitAsync("someone-else")).Should().BeNull();
    }

    private static async Task<ApplicationDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class MonitorFixture : IAsyncDisposable
    {
        private SqliteConnection _connection = null!;
        private ApplicationDbContext _db = null!;
        public ThreatMonitorService Service { get; private set; } = null!;
        public RecordingMailTransport Mail { get; } = new();
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CertificateExpires { get; set; }
        public IReadOnlyList<IndicatorMatch> FeedMatches { get; set; } = [];
        public IReadOnlyList<string> Hostnames { get; set; } = [];
        public IReadOnlyList<KnownExploitedVulnerability> Kev { get; set; } = [];

        public static async Task<MonitorFixture> CreateAsync()
        {
            var fixture = new MonitorFixture { _connection = new SqliteConnection("Data Source=:memory:") };
            await fixture._connection.OpenAsync();
            fixture._db = await CreateDbAsync(fixture._connection);
            fixture.Service = new ThreatMonitorService(
                fixture._db,
                new StubDomainIntel(fixture),
                new StubExposure(fixture),
                new StubFeeds(fixture),
                new StubVulnerabilities(fixture),
                new StubEmailSecurity(),
                new StubInventory(),
                new StubAdvisories(),
                fixture.Mail,
                Options.Create(new GrowthReportEmailOptions { Host = "smtp.example.com", FromAddress = "noreply@example.com" }),
                TimeProvider.System,
                NullLogger<ThreatMonitorService>.Instance) { NvdRequestSpacing = TimeSpan.Zero };
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubDomainIntel(MonitorFixture fixture) : IDomainIntelService
    {
        public Task<DomainIntelResult> InvestigateAsync(string target, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DomainIntelResult(target, false, null, [new DnsRecordInfo("A", "93.184.216.34")],
                fixture.CertificateExpires is { } expires ? new TlsCertificateInfo($"CN={target}", "CN=Test CA", expires.AddDays(-90), expires, [target]) : null,
                null, []));
    }

    private sealed class StubExposure(MonitorFixture fixture) : IExposureIntelService
    {
        public Task<ExposureIntelResult> InvestigateAsync(string target, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExposureIntelResult(target, false, null, null, null, fixture.Hostnames, [], null, []));
    }

    private sealed class StubFeeds(MonitorFixture fixture) : IMalwareFeedService
    {
        public Task<IReadOnlyList<BotnetC2Server>> GetBotnetC2ServersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BotnetC2Server>>([]);
        public Task<IReadOnlyList<MaliciousUrl>> GetRecentMaliciousUrlsAsync(int limit = 25, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaliciousUrl>>([]);
        public Task<IReadOnlyList<ThreatIndicator>> GetRecentIndicatorsAsync(int limit = 25, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ThreatIndicator>>([]);
        public Task<IReadOnlyList<MalwareSample>> GetRecentSamplesAsync(int limit = 25, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MalwareSample>>([]);
        public Task<IReadOnlyList<IndicatorMatch>> FindMatchesAsync(ClassifiedIndicator indicator, CancellationToken cancellationToken = default) => Task.FromResult(fixture.FeedMatches);
    }

    private sealed class StubVulnerabilities(MonitorFixture fixture) : IVulnerabilityIntelService
    {
        public Task<IReadOnlyList<KnownExploitedVulnerability>> GetKnownExploitedAsync(int limit = 25, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnownExploitedVulnerability>>(fixture.Kev.Take(limit).ToList());
        public Task<CveDetail?> GetCveDetailAsync(string cveId, CancellationToken cancellationToken = default) => Task.FromResult<CveDetail?>(null);
        public Task<IReadOnlyDictionary<string, double>> GetEpssScoresAsync(IEnumerable<string> cveIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());
        public Task<IReadOnlyList<CveSummary>> SearchRecentCvesAsync(string keyword, int days, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CveSummary>>([]);
    }

    private sealed class StubEmailSecurity : IEmailSecurityService
    {
        public Task<EmailSecurityReport> CheckAsync(string domain, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmailSecurityReport(domain, "A", [], "v=spf1 -all", null, null, [], null, null, [], []));
    }

    private sealed class StubInventory : IDependencyInventory
    {
        public IReadOnlyList<DependencyPackage> GetPackages() => [];
    }

    private sealed class StubAdvisories : IDependencyAdvisoryService
    {
        public Task<IReadOnlyList<DependencyAdvisory>> FindAdvisoriesAsync(IReadOnlyList<DependencyPackage> packages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DependencyAdvisory>>([]);
    }

    private sealed class RecordingMailTransport : IMailTransport
    {
        public List<MimeMessage> Sent { get; } = [];
        public MailRoute Describe(ISmtpTransportOptions options) => new(MailRouteKind.Smtp, "test");
        public Task SendAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
        public Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // IWikiService is large and ThreatIntelWorkspaceService only calls SavePageAsync, so the rest
    // of the interface is left unimplemented via DispatchProxy rather than a pile of stubs.
    private sealed class FakeWikiService
    {
        public List<WikiPageEditorModel> Saved { get; } = [];
        public IWikiService Service { get; }

        public FakeWikiService()
        {
            Service = System.Reflection.DispatchProxy.Create<IWikiService, WikiProxy>();
            ((WikiProxy)(object)Service).Owner = this;
        }
    }

    public class WikiProxy : System.Reflection.DispatchProxy
    {
        internal object? Owner { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IWikiService.SavePageAsync))
                throw new NotSupportedException($"{targetMethod?.Name} isn't used by these tests.");
            var editor = (WikiPageEditorModel)args![0]!;
            ((FakeWikiService)Owner!).Saved.Add(editor);
            return Task.FromResult(new WikiPage { Title = editor.Title, Slug = "investigation", BlocksJson = editor.BlocksJson });
        }
    }
}
