using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.ThreatIntel;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// Sample payloads are trimmed copies of the real responses captured live on 2026-10-04 (field
// names and casing exactly as served).
public sealed class ThreatIntelFeedServicesTests
{
    private const string KevJson = """
        {"title":"CISA Catalog of Known Exploited Vulnerabilities","catalogVersion":"2026.10.04","count":3,"vulnerabilities":[
          {"cveID":"CVE-2020-29583","vendorProject":"Zyxel","product":"Multiple Products","vulnerabilityName":"Zyxel Hard-Coded Credentials","dateAdded":"2021-11-03","shortDescription":"Old one.","requiredAction":"Apply updates per vendor instructions.","dueDate":"2022-05-03","knownRansomwareCampaignUse":"Unknown","notes":"","cwes":["CWE-522"]},
          {"cveID":"CVE-2024-3400","vendorProject":"Palo Alto Networks","product":"PAN-OS","vulnerabilityName":"PAN-OS Command Injection","dateAdded":"2024-04-12","shortDescription":"GlobalProtect injection.","requiredAction":"Apply mitigations.","dueDate":"2024-04-19","knownRansomwareCampaignUse":"Known","notes":"","cwes":[]},
          {"cveID":"","vendorProject":"Broken","product":"Row","vulnerabilityName":"Missing id","dateAdded":"2025-01-01","shortDescription":"","requiredAction":"","dueDate":"","knownRansomwareCampaignUse":"Unknown"}
        ]}
        """;

    private const string NvdJson = """
        {"resultsPerPage":1,"startIndex":0,"totalResults":1,"format":"NVD_CVE","version":"2.0","vulnerabilities":[{"cve":{
          "id":"CVE-2024-3400","published":"2024-04-12T08:15:06.230","descriptions":[{"lang":"es","value":"Spanish"},{"lang":"en","value":"A command injection in GlobalProtect."}],
          "metrics":{"cvssMetricV31":[
            {"source":"psirt@paloaltonetworks.com","type":"Secondary","cvssData":{"version":"3.1","baseScore":9.8,"baseSeverity":"CRITICAL"}},
            {"source":"nvd@nist.gov","type":"Primary","cvssData":{"version":"3.1","baseScore":10.0,"baseSeverity":"CRITICAL"}}]},
          "references":[{"url":"https://security.paloaltonetworks.com/CVE-2024-3400","source":"psirt","tags":["Vendor Advisory"]}]}}]}
        """;

    private const string EpssJson = """
        {"status":"OK","status-code":200,"version":"1.0","access":"public","total":1,"offset":0,"limit":100,"data":[{"cve":"CVE-2024-3400","epss":"0.943580000","percentile":"0.999120000","date":"2026-10-04"}]}
        """;

    [Fact]
    public async Task Kev_ShouldBeNewestFirst_SkipRowsWithoutAnId_AndReadTheRansomwareFlag()
    {
        var service = new VulnerabilityIntelService(Client(Route(("cisa.gov", Ok(KevJson)))), Cache(), NullLogger<VulnerabilityIntelService>.Instance);

        var entries = await service.GetKnownExploitedAsync();

        entries.Select(e => e.CveId).Should().Equal("CVE-2024-3400", "CVE-2020-29583");
        entries[0].KnownRansomwareUse.Should().BeTrue();
        entries[0].DueDate.Should().Be(new DateOnly(2024, 4, 19));
        entries[1].KnownRansomwareUse.Should().BeFalse();
    }

    [Fact]
    public async Task CveDetail_ShouldCombineNvdPrimaryScore_EpssStrings_AndKevMembership()
    {
        var service = new VulnerabilityIntelService(
            Client(Route(("cisa.gov", Ok(KevJson)), ("nvd.nist.gov", Ok(NvdJson)), ("api.first.org", Ok(EpssJson)))),
            Cache(), NullLogger<VulnerabilityIntelService>.Instance);

        var detail = await service.GetCveDetailAsync(" cve-2024-3400 ");

        detail.Should().NotBeNull();
        detail!.CveId.Should().Be("CVE-2024-3400");
        detail.Description.Should().Be("A command injection in GlobalProtect.");
        detail.CvssScore.Should().Be(10.0, "NVD's own Primary assessment wins over a vendor's Secondary one");
        detail.CvssSeverity.Should().Be("CRITICAL");
        detail.EpssScore.Should().BeApproximately(0.94358, 0.000001);
        detail.EpssPercentile.Should().BeApproximately(0.99912, 0.000001);
        detail.KnownExploited.Should().NotBeNull();
        detail.KnownExploited!.KnownRansomwareUse.Should().BeTrue();
        detail.References.Should().ContainSingle();
        detail.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task CveDetail_ShouldKeepEpssAndKev_WhenNvdIsDown_AndRejectMalformedIds()
    {
        var service = new VulnerabilityIntelService(
            Client(Route(("cisa.gov", Ok(KevJson)), ("nvd.nist.gov", Status(HttpStatusCode.ServiceUnavailable)), ("api.first.org", Ok(EpssJson)))),
            Cache(), NullLogger<VulnerabilityIntelService>.Instance);

        (await service.GetCveDetailAsync("not-a-cve")).Should().BeNull();
        var detail = await service.GetCveDetailAsync("CVE-2024-3400");

        detail!.Description.Should().BeNull();
        detail.EpssScore.Should().NotBeNull();
        detail.KnownExploited.Should().NotBeNull();
        detail.Errors.Should().ContainSingle(error => error.Contains("NVD returned 503"));
    }

    [Fact]
    public async Task MalwareFeeds_ShouldParseEachAbuseChFormat()
    {
        const string feodo = """
            [{"ip_address":"162.243.103.246","port":8080,"status":"offline","hostname":null,"as_number":14061,"as_name":"DIGITALOCEAN-ASN","country":"US","first_seen":"2022-06-04 21:24:53","last_online":"2026-03-07","malware":"Emotet"},
             {"ip_address":"50.16.16.211","port":443,"status":"online","hostname":null,"as_number":14618,"as_name":"AMAZON-AES","country":"US","first_seen":"2025-12-30 13:56:31","last_online":"2026-03-12","malware":"QakBot"}]
            """;
        const string urlhaus = """
            {"3928801":[{"dateadded":"2026-10-05 02:02:13 UTC","url":"http://180.244.15.54:39118/bin.sh","url_status":"online","last_online":"2026-10-05 02:02:13 UTC","threat":"malware_download","tags":["mirai"],"urlhaus_link":"https://urlhaus.abuse.ch/url/3928801/","reporter":"x"}],
             "3928700":[{"dateadded":"2026-10-04 01:00:00 UTC","url":"http://example.test/a.exe","url_status":"offline","last_online":null,"threat":"malware_download","tags":null,"urlhaus_link":"https://urlhaus.abuse.ch/url/3928700/","reporter":"x"}]}
            """;
        const string threatfox = """
            {"1952028":[{"ioc_value":"188.165.224.129:8084","ioc_type":"ip:port","threat_type":"botnet_cc","malware":"win.vshell","malware_alias":null,"malware_printable":"VShell","first_seen_utc":"2026-10-05 02:05:04","last_seen_utc":null,"confidence_level":100,"is_compromised":true,"reference":null,"tags":"vshell, c2","anonymous":1,"reporter":"anonymous"}]}
            """;
        const string bazaar = """
            ################################################################
            # MalwareBazaar recent malware samples (CSV)                   #
            ################################################################
            # "first_seen_utc","sha256_hash","md5_hash","sha1_hash","reporter","file_name","file_type_guess","mime_type","signature","clamav","vtpercent","imphash","ssdeep","tlsh"
            "2026-10-05 00:42:15", "b7689b12e1b94e628c8398ec86a6ad5ba564f107c5085464f3d03f408959545b", "46de", "cde1", "abuse_ch", "i686", "elf", "application/x-executable", "n/a", "n/a", "n/a", "n/a", "384:x", "T1"
            "2026-10-05 00:38:02", "d07b4210db5c2610daedc2831915af6c4e9f7966739ff789a0af2a18bb5dca66", "1486", "fa67", "abuse_ch", "kushnet.x64-test", "elf", "application/x-executable", "Mirai", "n/a", "n/a", "n/a", "12288:x", "T1"
            """;
        var service = new MalwareFeedService(
            Client(Route(("feodotracker", Ok(feodo)), ("urlhaus", Ok(urlhaus)), ("threatfox", Ok(threatfox)), ("bazaar", Ok(bazaar)))),
            Cache(), NullLogger<MalwareFeedService>.Instance);

        var c2 = await service.GetBotnetC2ServersAsync();
        c2.Select(s => s.Malware).Should().Equal("QakBot", "Emotet"); // online first
        c2[0].LastOnline.Should().Be(new DateOnly(2026, 3, 12));

        var urls = await service.GetRecentMaliciousUrlsAsync();
        urls.Select(u => u.Url).Should().Equal("http://180.244.15.54:39118/bin.sh", "http://example.test/a.exe");
        urls[0].IsOnline.Should().BeTrue();
        urls[0].Tags.Should().Equal("mirai");
        urls[1].Tags.Should().BeEmpty(); // "tags": null
        urls[0].DateAdded.Should().Be(new DateTimeOffset(2026, 10, 5, 2, 2, 13, TimeSpan.Zero));

        var iocs = await service.GetRecentIndicatorsAsync();
        iocs.Should().ContainSingle();
        iocs[0].Malware.Should().Be("VShell");
        iocs[0].Tags.Should().Equal("vshell", "c2"); // comma-separated string, not an array
        iocs[0].ReferenceUrl.Should().Be("https://threatfox.abuse.ch/ioc/1952028/");

        var samples = await service.GetRecentSamplesAsync();
        samples.Should().HaveCount(2);
        samples[0].Signature.Should().BeNull("\"n/a\" means unclassified");
        samples[1].Signature.Should().Be("Mirai");
        samples[1].FileName.Should().Be("kushnet.x64-test");
        samples[1].Md5.Should().Be("1486");

        // Indicator matching searches every feed's full recent list.
        var byHost = await service.FindMatchesAsync(ThreatIndicatorClassifier.Classify("180.244.15.54"));
        byHost.Should().ContainSingle(m => m.Feed == "URLhaus" && m.MatchedValue == "http://180.244.15.54:39118/bin.sh");
        var byIpPort = await service.FindMatchesAsync(ThreatIndicatorClassifier.Classify("188[.]165[.]224[.]129"));
        byIpPort.Should().ContainSingle(m => m.Feed == "ThreatFox" && m.Description.StartsWith("VShell"));
        var byC2 = await service.FindMatchesAsync(ThreatIndicatorClassifier.Classify("50.16.16.211"));
        byC2.Should().ContainSingle(m => m.Feed == "Feodo Tracker" && m.MatchedValue == "50.16.16.211:443");
        var bySha = await service.FindMatchesAsync(ThreatIndicatorClassifier.Classify("D07B4210DB5C2610DAEDC2831915AF6C4E9F7966739FF789A0AF2A18BB5DCA66"));
        bySha.Should().ContainSingle(m => m.Feed == "MalwareBazaar" && m.Description.StartsWith("Mirai"));
        var byMd5 = await service.FindMatchesAsync(new ClassifiedIndicator(IndicatorKind.Md5, "1486", null));
        byMd5.Should().ContainSingle(m => m.Feed == "MalwareBazaar");
        (await service.FindMatchesAsync(ThreatIndicatorClassifier.Classify("grantwatson.dev"))).Should().BeEmpty();
    }

    [Fact]
    public async Task EpssScores_ShouldBatchAndCache_AndSkipUnscoredCves()
    {
        var requests = new List<string>();
        var service = new VulnerabilityIntelService(Client(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return Ok(EpssJson);
        }), Cache(), NullLogger<VulnerabilityIntelService>.Instance);

        var scores = await service.GetEpssScoresAsync(["CVE-2024-3400", "cve-2021-44228", "not-a-cve"]);
        scores.Should().ContainSingle().Which.Value.Should().BeApproximately(0.94358, 0.00001);
        requests.Should().ContainSingle().Which.Should().Contain("cve=CVE-2024-3400,CVE-2021-44228");

        await service.GetEpssScoresAsync(["CVE-2024-3400", "CVE-2021-44228"]);
        requests.Should().HaveCount(1, "scored and unscored answers are both cached");
    }

    [Fact]
    public async Task SearchRecentCves_ShouldSendADateBoundedKeywordSearch_AndParseScores()
    {
        string? url = null;
        var service = new VulnerabilityIntelService(Client(request =>
        {
            url = request.RequestUri!.ToString();
            return Ok(NvdJson);
        }), Cache(), NullLogger<VulnerabilityIntelService>.Instance);

        var results = await service.SearchRecentCvesAsync("pan-os", 500);

        results.Should().ContainSingle();
        results[0].CveId.Should().Be("CVE-2024-3400");
        results[0].CvssScore.Should().Be(10.0);
        url.Should().Contain("keywordSearch=pan-os").And.Contain("pubStartDate=").And.Contain("pubEndDate=");
        var start = DateTimeOffset.Parse(System.Web.HttpUtility.ParseQueryString(new Uri(url!).Query)["pubStartDate"]!);
        (DateTimeOffset.UtcNow - start).TotalDays.Should().BeApproximately(120, 0.1, "NVD rejects ranges over 120 days");
    }

    [Fact]
    public async Task MalwareFeeds_ShouldReturnEmpty_WhenAFeedFails()
    {
        var service = new MalwareFeedService(Client(_ => Status(HttpStatusCode.BadGateway)), Cache(), NullLogger<MalwareFeedService>.Instance);

        (await service.GetRecentMaliciousUrlsAsync()).Should().BeEmpty();
        (await service.GetBotnetC2ServersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Breaches_ShouldBeNewestAddedFirst_WithoutFabricatedOrSpamLists()
    {
        const string hibp = """
            [{"Name":"Old","Title":"Old Co","Domain":"old.test","BreachDate":"2019-01-01","AddedDate":"2019-02-01T00:00:00Z","PwnCount":10,"DataClasses":["Email addresses"],"IsVerified":true,"IsFabricated":false,"IsSpamList":false},
             {"Name":"Medela","Title":"Medela","Domain":"medela.com","BreachDate":"2026-09-07","AddedDate":"2026-09-30T11:48:28Z","PwnCount":423947,"DataClasses":["Email addresses","Names"],"IsVerified":true,"IsFabricated":false,"IsSpamList":false},
             {"Name":"Fake","Title":"Fake","Domain":"","BreachDate":"2026-09-01","AddedDate":"2026-10-01T00:00:00Z","PwnCount":5,"DataClasses":[],"IsVerified":false,"IsFabricated":true,"IsSpamList":false},
             {"Name":"Spam","Title":"Spam","Domain":"","BreachDate":"2026-09-01","AddedDate":"2026-10-02T00:00:00Z","PwnCount":5,"DataClasses":[],"IsVerified":false,"IsFabricated":false,"IsSpamList":true}]
            """;
        var service = new DataBreachFeedService(Client(Route(("haveibeenpwned", Ok(hibp)))), Cache(), NullLogger<DataBreachFeedService>.Instance);

        var breaches = await service.GetRecentBreachesAsync();

        breaches.Select(b => b.Name).Should().Equal("Medela", "Old");
        breaches[0].AccountCount.Should().Be(423947);
        breaches[0].BreachDate.Should().Be(new DateOnly(2026, 9, 7));
        breaches[0].ReferenceUrl.Should().Be("https://haveibeenpwned.com/Breach/Medela");
    }

    [Fact]
    public async Task Exposure_ShouldSkipPrivateAddresses_WithoutAnyRequest()
    {
        var requests = 0;
        var service = new ExposureIntelService(Client(_ => { requests++; return Ok("{}"); }), Cache(), NullLogger<ExposureIntelService>.Instance);

        var result = await service.InvestigateAsync("10.0.0.1");

        requests.Should().Be(0);
        result.IpExposure.Should().BeNull();
        result.Errors.Should().ContainSingle(error => error.Contains("Private or reserved"));
    }

    [Fact]
    public async Task Exposure_ForAnIp_ShouldReadInternetDb_TorExits_AndSpamhausDrop()
    {
        const string internetDb = """{"cpes":["cpe:/a:cloudflare:cloudflare"],"hostnames":["one.one.one.one"],"ip":"1.10.16.5","ports":[443,53],"tags":[],"vulns":["CVE-2023-1","CVE-2024-2"]}""";
        const string drop = """
            {"cidr":"1.10.16.0/20","sblid":"SBL256894","rir":"apnic"}
            {"cidr":"1.19.0.0/16","sblid":"SBL434604","rir":"apnic"}
            {"type":"metadata","timestamp":1791054842,"size":103249,"records":2}
            """;
        var service = new ExposureIntelService(
            Client(Route(("internetdb", Ok(internetDb)), ("torproject", Ok("171.25.193.25\n1.10.16.5\n")), ("drop_v4", Ok(drop)))),
            Cache(), NullLogger<ExposureIntelService>.Instance);

        var result = await service.InvestigateAsync("1.10.16.5");

        result.IsIpAddress.Should().BeTrue();
        result.IpExposure!.Ports.Should().Equal(53, 443);
        result.IpExposure.Vulnerabilities.Should().Equal("CVE-2024-2", "CVE-2023-1");
        result.IsTorExitNode.Should().BeTrue();
        result.SpamhausDrop.Should().Be(new GwsBusinessSuite.Application.ThreatIntel.SpamhausDropListing("1.10.16.0/20", "SBL256894"));
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Exposure_ForAnIpShodanHasNotSeen_ShouldBeEmptyNotAnError()
    {
        var service = new ExposureIntelService(
            Client(Route(("internetdb", Status(HttpStatusCode.NotFound)), ("torproject", Ok("171.25.193.25\n")), ("drop_v4", Ok("")))),
            Cache(), NullLogger<ExposureIntelService>.Instance);

        var result = await service.InvestigateAsync("8.8.4.4");

        result.IpExposure!.Ports.Should().BeEmpty();
        result.IsTorExitNode.Should().BeFalse();
        result.SpamhausDrop.Should().BeNull();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Exposure_ForADomain_ShouldUseCertificateLogs_UrlscanPageDomain_AndTheArchive()
    {
        const string certspotter = """
            [{"id":"1","dns_names":["*.grantwatson.dev","grantwatson.dev"],"not_before":"2026-08-31T01:08:54Z"},
             {"id":"2","dns_names":["www.grantwatson.dev","unrelated.example"],"not_before":"2026-07-01T00:00:00Z"}]
            """;
        const string urlscan = """
            {"results":[{"task":{"visibility":"public","method":"api","domain":"grantwatson.dev","time":"2026-10-05T02:12:53.741Z","uuid":"01a1-uuid","url":"https://grantwatson.dev/"},
              "page":{"country":"US","server":"cloudflare","ip":"104.21.1.1","title":"Grant Watson","url":"https://grantwatson.dev/","domain":"grantwatson.dev"}}],"total":1}
            """;
        string? urlscanQuery = null;
        var service = new ExposureIntelService(Client(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("certspotter")) return Ok(certspotter);
            if (url.Contains("urlscan")) { urlscanQuery = request.RequestUri.Query; return Ok(urlscan); }
            if (url.Contains("wayback/available")) return Ok("""{"url":"grantwatson.dev","archived_snapshots":{"closest":{"status":"200","available":true,"url":"http://web.archive.org/web/20260612141837/https://www.grantwatson.dev/","timestamp":"20260612141837"}}}""");
            if (url.Contains("web.archive.org")) return Ok("""[["timestamp"],["20201101000403"]]""");
            return Status(HttpStatusCode.NotFound);
        }), Cache(), NullLogger<ExposureIntelService>.Instance);

        var result = await service.InvestigateAsync("https://GrantWatson.dev/about");

        result.Target.Should().Be("grantwatson.dev");
        result.CertificateHostnames.Should().Equal("*.grantwatson.dev", "grantwatson.dev", "www.grantwatson.dev");
        Uri.UnescapeDataString(urlscanQuery!).Should().Contain("q=page.domain:grantwatson.dev");
        result.RecentScans.Should().ContainSingle();
        result.RecentScans[0].ResultUrl.Should().Be("https://urlscan.io/result/01a1-uuid/");
        result.Archive!.FirstSnapshot.Should().Be(new DateTimeOffset(2020, 11, 1, 0, 4, 3, TimeSpan.Zero));
        result.Archive.LastSnapshot.Should().Be(new DateTimeOffset(2026, 6, 12, 14, 18, 37, TimeSpan.Zero));
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Exposure_ShouldRejectSomethingThatIsNeitherADomainNorAnIp()
    {
        var requests = 0;
        var service = new ExposureIntelService(Client(_ => { requests++; return Ok("[]"); }), Cache(), NullLogger<ExposureIntelService>.Instance);

        var result = await service.InvestigateAsync("not a domain");

        requests.Should().Be(0);
        result.Errors.Should().ContainSingle();
    }

    private static MemoryCache Cache() => new(new MemoryCacheOptions());

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent(string.Empty) };

    // First route whose fragment appears in the request URL answers; anything else is a 404.
    private static Func<HttpRequestMessage, HttpResponseMessage> Route(params (string UrlFragment, HttpResponseMessage Response)[] routes) =>
        request =>
        {
            var url = request.RequestUri!.ToString();
            foreach (var (fragment, response) in routes)
            {
                if (url.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return Clone(response);
            }
            return Status(HttpStatusCode.NotFound);
        };

    // A response's content can only be read once; tests may hit the same route repeatedly.
    private static HttpResponseMessage Clone(HttpResponseMessage response) => new(response.StatusCode)
    {
        Content = new StringContent(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), Encoding.UTF8, "application/json")
    };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) => new(new RoutingHandler(responder));

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
