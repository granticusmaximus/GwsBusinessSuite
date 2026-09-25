using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using GwsBusinessSuite.Application.ThreatIntel;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class DomainIntelServiceTests
{
    // Trimmed but structurally real - shapes confirmed via direct curl of rdap.org during
    // implementation (a .com domain redirects to Verisign's RDAP server; an IP redirects to
    // ARIN's, which reports the allocated org via a top-level "name" field, not a
    // "registrar"-roled entity the way domain RDAP does).
    private const string DomainRdapJson = """
        {
          "objectClassName": "domain",
          "ldhName": "EXAMPLE.COM",
          "status": ["client delete prohibited", "client transfer prohibited"],
          "entities": [
            { "objectClassName": "entity", "roles": ["registrar"],
              "vcardArray": ["vcard", [["version", {}, "text", "4.0"], ["fn", {}, "text", "Example Registrar Inc."]]] }
          ],
          "events": [
            { "eventAction": "registration", "eventDate": "1995-08-14T04:00:00Z" },
            { "eventAction": "expiration", "eventDate": "2027-08-13T04:00:00Z" }
          ],
          "nameservers": [
            { "objectClassName": "nameserver", "ldhName": "NS1.EXAMPLE.COM" },
            { "objectClassName": "nameserver", "ldhName": "NS2.EXAMPLE.COM" }
          ]
        }
        """;

    private const string IpRdapJson = """
        {
          "name": "GOOGLE",
          "startAddress": "8.8.8.0",
          "endAddress": "8.8.8.255",
          "status": ["active"]
        }
        """;

    private const string FocsecJson = """{"is_vpn": false, "is_proxy": false, "is_tor": false, "is_bot": true, "iso_code": "US"}""";

    [Fact]
    public async Task InvestigateAsync_ForADomain_ShouldParseRegistrationDnsAndTls()
    {
        using var cert = CreateTestCertificate("example.com");
        var service = CreateService(
            rdapDomainJson: DomainRdapJson,
            dnsResolver: new FakeDnsResolver(_ => [IPAddress.Parse("93.184.216.34")]),
            tlsFetcher: new FakeTlsCertificateFetcher(_ => cert));

        var result = await service.InvestigateAsync("example.com");

        result.IsIpAddress.Should().BeFalse();
        result.Registration.Should().NotBeNull();
        result.Registration!.Registrar.Should().Be("Example Registrar Inc.");
        result.Registration.Registered.Should().Be(DateTimeOffset.Parse("1995-08-14T04:00:00Z"));
        result.Registration.Expires.Should().Be(DateTimeOffset.Parse("2027-08-13T04:00:00Z"));
        result.Registration.Nameservers.Should().BeEquivalentTo(["NS1.EXAMPLE.COM", "NS2.EXAMPLE.COM"]);
        result.Registration.Statuses.Should().Contain("client delete prohibited");

        result.DnsRecords.Should().ContainSingle(r => r.RecordType == "A" && r.Value == "93.184.216.34");

        result.TlsCertificate.Should().NotBeNull();
        result.TlsCertificate!.SubjectAlternativeNames.Should().Contain("example.com");

        result.IpReputation.Should().BeNull("IP reputation only applies when the target is an IP");
    }

    [Fact]
    public async Task InvestigateAsync_ForAnIp_ShouldSkipDnsAndTls_AndQueryFocsec()
    {
        var dnsCalls = 0;
        var tlsCalls = 0;
        var service = CreateService(
            rdapIpJson: IpRdapJson,
            focsecJson: FocsecJson,
            focsecApiKey: "TEST_FOCSEC_KEY",
            dnsResolver: new FakeDnsResolver(_ => { dnsCalls++; return []; }),
            tlsFetcher: new FakeTlsCertificateFetcher(_ => { tlsCalls++; return null; }));

        var result = await service.InvestigateAsync("8.8.8.8");

        result.IsIpAddress.Should().BeTrue();
        dnsCalls.Should().Be(0, "DNS lookups don't apply to a raw IP target");
        tlsCalls.Should().Be(0, "a raw IP target has no hostname to inspect a TLS certificate against");

        result.Registration.Should().NotBeNull();
        result.Registration!.Registrar.Should().Be("GOOGLE");
        result.Registration.Statuses.Should().Contain("active");

        result.IpReputation.Should().NotBeNull();
        result.IpReputation!.IsBot.Should().BeTrue();
        result.IpReputation.IsVpn.Should().BeFalse();
        result.IpReputation.CountryCode.Should().Be("US");
    }

    [Fact]
    public async Task InvestigateAsync_ShouldReturnNullIpReputation_WhenNoFocsecKeyIsConfigured()
    {
        var focsecCalls = 0;
        var service = CreateService(
            rdapIpJson: IpRdapJson,
            focsecJson: FocsecJson,
            focsecApiKey: "",
            onFocsecRequest: () => focsecCalls++);

        var result = await service.InvestigateAsync("8.8.8.8");

        result.IpReputation.Should().BeNull();
        focsecCalls.Should().Be(0, "an unconfigured Focsec key must not make any HTTP call at all");
    }

    [Fact]
    public async Task InvestigateAsync_ShouldRecordAnError_RatherThanThrow_WhenRdapFails()
    {
        var service = CreateService(rdapStatus: HttpStatusCode.NotFound);

        var result = await service.InvestigateAsync("nonexistent-domain-example.test");

        result.Registration.Should().BeNull();
        result.Errors.Should().Contain(e => e.Contains("RDAP"));
    }

    [Fact]
    public async Task InvestigateAsync_ShouldRecordAnError_RatherThanThrow_WhenDnsLookupFails()
    {
        var service = CreateService(
            rdapDomainJson: DomainRdapJson,
            dnsResolver: new ThrowingDnsResolver(),
            tlsFetcher: new FakeTlsCertificateFetcher(_ => null));

        var result = await service.InvestigateAsync("example.com");

        result.DnsRecords.Should().BeEmpty();
        result.Errors.Should().Contain(e => e.Contains("DNS lookup failed"));
    }

    [Fact]
    public async Task InvestigateAsync_ShouldRecordAnError_RatherThanThrow_WhenTlsFetchFails()
    {
        var service = CreateService(
            rdapDomainJson: DomainRdapJson,
            dnsResolver: new FakeDnsResolver(_ => []),
            tlsFetcher: new ThrowingTlsCertificateFetcher());

        var result = await service.InvestigateAsync("example.com");

        result.TlsCertificate.Should().BeNull();
        result.Errors.Should().Contain(e => e.Contains("TLS certificate fetch failed"));
    }

    private static X509Certificate2 CreateTestCertificate(string dnsName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(dnsName);
        request.CertificateExtensions.Add(sanBuilder.Build());

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(89));
    }

    private static DomainIntelService CreateService(
        string? rdapDomainJson = null,
        string? rdapIpJson = null,
        HttpStatusCode rdapStatus = HttpStatusCode.OK,
        string? focsecJson = null,
        string focsecApiKey = "",
        IDnsResolver? dnsResolver = null,
        ITlsCertificateFetcher? tlsFetcher = null,
        Action? onFocsecRequest = null)
    {
        var handler = new RoutingHandler(rdapDomainJson, rdapIpJson, rdapStatus, focsecJson, onFocsecRequest);
        var http = new HttpClient(handler);

        return new DomainIntelService(
            http,
            dnsResolver ?? new FakeDnsResolver(_ => []),
            tlsFetcher ?? new FakeTlsCertificateFetcher(_ => null),
            Options.Create(new ThreatIntelOptions { FocsecApiKey = focsecApiKey }),
            NullLogger<DomainIntelService>.Instance);
    }

    private sealed class FakeDnsResolver(Func<string, IReadOnlyList<IPAddress>> resolve) : IDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostName, CancellationToken cancellationToken = default) =>
            Task.FromResult(resolve(hostName));
    }

    private sealed class ThrowingDnsResolver : IDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostName, CancellationToken cancellationToken = default) =>
            throw new SocketException();
    }

    private sealed class FakeTlsCertificateFetcher(Func<string, X509Certificate2?> fetch) : ITlsCertificateFetcher
    {
        public Task<X509Certificate2?> FetchAsync(string hostName, CancellationToken cancellationToken = default) =>
            Task.FromResult(fetch(hostName));
    }

    private sealed class ThrowingTlsCertificateFetcher : ITlsCertificateFetcher
    {
        public Task<X509Certificate2?> FetchAsync(string hostName, CancellationToken cancellationToken = default) =>
            throw new IOException("simulated handshake failure");
    }

    private sealed class RoutingHandler(
        string? rdapDomainJson,
        string? rdapIpJson,
        HttpStatusCode rdapStatus,
        string? focsecJson,
        Action? onFocsecRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri!.AbsolutePath;

            if (host.Contains("focsec.com"))
            {
                onFocsecRequest?.Invoke();
                return Task.FromResult(focsecJson is null
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : JsonResponse(focsecJson));
            }

            // rdap.org
            if (rdapStatus != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(rdapStatus));

            var json = path.StartsWith("/ip/") ? rdapIpJson : rdapDomainJson;
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(json));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
