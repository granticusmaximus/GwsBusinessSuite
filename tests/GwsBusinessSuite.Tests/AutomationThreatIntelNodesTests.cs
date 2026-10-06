using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.Extensions.DependencyInjection;

namespace GwsBusinessSuite.Tests;

public sealed class AutomationThreatIntelNodesTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task KnownExploited_ShouldOnlyReturnEntriesAddedWithinSinceDays()
    {
        var registry = Registry(new FakeVulnerabilityService(
            Kev("CVE-2026-0003", Today),
            Kev("CVE-2026-0002", Today.AddDays(-1)),
            Kev("CVE-2025-0001", Today.AddDays(-30))));

        var result = await registry.ExecuteAsync(NewNode("threatintel.knownExploited", """{"sinceDays":1,"limit":25}"""), Input("""{"keep":"me"}"""), null);

        var output = result.Outputs["main"][0];
        output.GetProperty("keep").GetString().Should().Be("me", "the node adds to the item rather than replacing it");
        output.GetProperty("vulnerabilityCount").GetInt32().Should().Be(2);
        output.GetProperty("vulnerabilities").EnumerateArray().Select(v => v.GetProperty("cveId").GetString())
            .Should().Equal("CVE-2026-0003", "CVE-2026-0002");
    }

    [Fact]
    public async Task LookupCve_ShouldResolveTheExpression_AndReportKevMembership()
    {
        var registry = Registry(new FakeVulnerabilityService(Kev("CVE-2024-3400", Today)));

        var result = await registry.ExecuteAsync(NewNode("threatintel.lookupCve", """{"cveId":"{{ $json.cveId }}"}"""), Input("""{"cveId":"CVE-2024-3400"}"""), null);

        var cve = result.Outputs["main"][0].GetProperty("cve");
        cve.GetProperty("cveId").GetString().Should().Be("CVE-2024-3400");
        cve.GetProperty("cvssScore").GetDouble().Should().Be(10.0);
        cve.GetProperty("knownExploited").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task LookupCve_ShouldFailClearly_ForSomethingThatIsNotACveId()
    {
        var registry = Registry(new FakeVulnerabilityService());

        var act = () => registry.ExecuteAsync(NewNode("threatintel.lookupCve", """{"cveId":"hello"}"""), Input("{}"), null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a CVE id*");
    }

    [Fact]
    public async Task CheckIndicator_ShouldFlattenTheExposureResult()
    {
        var exposure = new ExposureIntelResult("1.10.16.5", true,
            new IpExposureInfo([22, 443], ["CVE-2023-1"], [], [], []), true, new SpamhausDropListing("1.10.16.0/20", "SBL256894"),
            [], [], null, []);
        var registry = Registry(exposure: new FakeExposureService(exposure));

        var result = await registry.ExecuteAsync(NewNode("threatintel.checkIndicator", """{"target":"{{ $json.ip }}"}"""), Input("""{"ip":"1.10.16.5"}"""), null);

        var indicator = result.Outputs["main"][0].GetProperty("indicator");
        indicator.GetProperty("openPorts").EnumerateArray().Select(p => p.GetInt32()).Should().Equal(22, 443);
        indicator.GetProperty("isTorExitNode").GetBoolean().Should().BeTrue();
        indicator.GetProperty("onSpamhausDrop").GetBoolean().Should().BeTrue();
        indicator.GetProperty("spamhausSblId").GetString().Should().Be("SBL256894");
    }

    private static AutomationNodeRegistry Registry(IVulnerabilityIntelService? vulnerabilities = null, IExposureIntelService? exposure = null)
    {
        var services = new ServiceCollection();
        if (vulnerabilities is not null) services.AddSingleton(vulnerabilities);
        if (exposure is not null) services.AddSingleton(exposure);
        return new AutomationNodeRegistry(new NoHttpClient(), serviceProvider: services.BuildServiceProvider());
    }

    private static KnownExploitedVulnerability Kev(string cveId, DateOnly added) =>
        new(cveId, "Vendor", "Product", "Name", "Description", "Patch it.", added, added.AddDays(21), false);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    private static AutomationNodeSnapshot NewNode(string typeKey, string parametersJson) => new(
        Guid.NewGuid(), typeKey, typeKey, 1, parametersJson, null, false, false, false, 1, 0, 0);

    private sealed class FakeVulnerabilityService(params KnownExploitedVulnerability[] kev) : IVulnerabilityIntelService
    {
        public Task<IReadOnlyList<KnownExploitedVulnerability>> GetKnownExploitedAsync(int limit = 25, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnownExploitedVulnerability>>(kev.OrderByDescending(k => k.DateAdded).Take(limit).ToList());

        public Task<CveDetail?> GetCveDetailAsync(string cveId, CancellationToken cancellationToken = default) =>
            Task.FromResult(cveId.StartsWith("CVE-", StringComparison.Ordinal)
                ? new CveDetail(cveId, "desc", null, 10.0, "CRITICAL", "3.1", 0.9, 0.99, kev.FirstOrDefault(k => k.CveId == cveId), [], [])
                : null);

        public Task<IReadOnlyDictionary<string, double>> GetEpssScoresAsync(IEnumerable<string> cveIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());

        public Task<IReadOnlyList<CveSummary>> SearchRecentCvesAsync(string keyword, int days, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CveSummary>>([]);
    }

    private sealed class FakeExposureService(ExposureIntelResult result) : IExposureIntelService
    {
        public Task<ExposureIntelResult> InvestigateAsync(string target, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class NoHttpClient : IAutomationHttpClient
    {
        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("These nodes must not make raw HTTP calls.");
    }
}
