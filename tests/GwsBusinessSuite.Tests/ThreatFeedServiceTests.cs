using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// Unit 7 (06-11-26): Threat Intelligence's fresh feeds. The CIRCL and GitHub fixtures are trimmed
// from the live feeds of 06-11-26; the EDGAR one follows its full-text search response, which
// hasn't been called live yet (it needs SecEdgar:ContactEmail).
public sealed class ThreatFeedServiceTests
{
    [Fact]
    public void NewCves_ShouldKeepTheLast72Hours_NewestFirst_WithTheCnasScore()
    {
        const string json = """
            [
              {"cveMetadata":{"cveId":"CVE-2026-92080","state":"PUBLISHED","datePublished":"2026-11-06T13:01:29.523Z"},
               "containers":{"cna":{"affected":[{"vendor":"TÜBİTAK BİLGEM","product":"pardus-boot-repair"}],
                 "descriptions":[{"lang":"en","value":"Path Traversal vulnerability in pardus-boot-repair."}],
                 "metrics":[{"format":"CVSS","cvssV3_1":{"baseScore":7.5,"baseSeverity":"HIGH"}}]}}},
              {"cveMetadata":{"cveId":"CVE-2026-118560","state":"PUBLISHED","datePublished":"2026-10-30T03:09:01.410Z"},
               "containers":{"cna":{"affected":[{"vendor":"Drupal","product":"Commerce Stripe"}],"descriptions":[{"lang":"en","value":"XSS"}]}}},
              {"cveMetadata":{"cveId":"CVE-2026-115952","state":"PUBLISHED","datePublished":"2026-11-06T13:00:09.134Z"},
               "containers":{"cna":{"affected":[{"vendor":"n/a","product":"n/a"}],"descriptions":[{"lang":"en","value":"Cross-site Scripting in Forminator."}],
                 "metrics":[{"cvssV4_0":{"baseScore":9.3,"baseSeverity":"CRITICAL"}},{"cvssV3_1":{"baseScore":6.1,"baseSeverity":"MEDIUM"}}]}}},
              {"cveMetadata":{"cveId":"CVE-2026-1","state":"REJECTED","datePublished":"2026-11-06T12:00:00Z"},"containers":{"cna":{}}}
            ]
            """;

        var cves = ThreatFeedService.ParseNewCves(json, new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero));

        cves.Select(c => c.Id).Should().Equal(["CVE-2026-92080", "CVE-2026-115952"], "older and rejected records are left out, newest first");
        cves[0].Should().Match<NewCve>(c => c.Vendor == "TÜBİTAK BİLGEM" && c.Product == "pardus-boot-repair" && c.Score == 7.5 && c.Severity == "high");
        cves[1].Should().Match<NewCve>(c => c.Vendor == null && c.Product == null && c.Score == 9.3 && c.Severity == "critical", "CVSS 4 comes first, and 'n/a' isn't a vendor");
    }

    [Fact]
    public void MaliciousPackages_ShouldNameThePackage_AndSkipWithdrawnAdvisories()
    {
        const string json = """
            [
              {"ghsa_id":"GHSA-f64g-9rx5-4jxf","summary":"Malware in sickle-wrapper","published_at":"2026-11-06T05:43:40Z","withdrawn_at":null,
               "html_url":"https://github.com/advisories/GHSA-f64g-9rx5-4jxf","vulnerabilities":[{"package":{"ecosystem":"npm","name":"sickle-wrapper"}}]},
              {"ghsa_id":"GHSA-old","summary":"Withdrawn","published_at":"2026-11-01T00:00:00Z","withdrawn_at":"2026-11-02T00:00:00Z","html_url":"x","vulnerabilities":[]}
            ]
            """;

        var packages = ThreatFeedService.ParseMaliciousPackages(json);

        packages.Should().ContainSingle().Which.Should().Match<MaliciousPackage>(p => p.Id == "GHSA-f64g-9rx5-4jxf" && p.Ecosystem == "npm"
            && p.Name == "sickle-wrapper" && p.Url == "https://github.com/advisories/GHSA-f64g-9rx5-4jxf");
    }

    [Fact]
    public void Disclosures_ShouldLinkEachFilingsDocument()
    {
        const string json = """
            {"hits":{"total":{"value":2},"hits":[
              {"_id":"0001193125-26-412345:d123456d8k.htm","_source":{"ciks":["0000320193"],"display_names":["Example Corp  (EXMP)  (CIK 0000320193)"],"file_date":"2026-11-03","form":"8-K","items":["1.05","9.01"]}},
              {"_id":"0000950170-26-100001:form8-k.htm","_source":{"ciks":["0001018724"],"display_names":["Another Inc (CIK 0001018724)"],"file_date":"2026-11-05","form":"8-K"}}]}}
            """;

        var filings = ThreatFeedService.ParseDisclosures(json);

        filings.Select(f => f.Company).Should().Equal(["Another Inc", "Example Corp  (EXMP)"], "newest first, without the CIK suffix");
        filings[1].Url.Should().Be("https://www.sec.gov/Archives/edgar/data/320193/000119312526412345/d123456d8k.htm");
        filings[1].Filed.Should().Be(new DateOnly(2026, 11, 3));
    }
}
