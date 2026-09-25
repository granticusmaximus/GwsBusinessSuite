using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.ThreatIntel;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class OtxThreatIntelServiceTests
{
    private const string SampleJson = """
        {
          "count": 2,
          "results": [
            {
              "id": "abc123",
              "name": "Emotet resurgence",
              "description": "Fresh Emotet C2 infrastructure.",
              "author_name": "alienvault",
              "created": "2026-09-20T12:00:00Z",
              "tags": ["emotet", "malware"],
              "indicators": [{"id": 1}, {"id": 2}, {"id": 3}]
            },
            {
              "id": "missing-name",
              "description": "Should be skipped - no name."
            }
          ]
        }
        """;

    [Fact]
    public async Task GetPulsesAsync_ShouldReturnEmpty_WhenNoApiKeyIsConfigured()
    {
        var service = CreateService(apiKey: "", _ => new HttpResponseMessage(HttpStatusCode.OK), out var requestCount);

        var result = await service.GetPulsesAsync(null);

        result.Should().BeEmpty();
        requestCount().Should().Be(0, "an unconfigured service must not make any HTTP call at all");
    }

    [Fact]
    public async Task GetPulsesAsync_ShouldMapPulses_WithCompleteData()
    {
        var service = CreateService("TEST_KEY", _ => JsonResponse(SampleJson), out _);

        var result = await service.GetPulsesAsync(null);

        result.Should().ContainSingle();
        var pulse = result[0];
        pulse.Id.Should().Be("abc123");
        pulse.Name.Should().Be("Emotet resurgence");
        pulse.Description.Should().Be("Fresh Emotet C2 infrastructure.");
        pulse.AuthorName.Should().Be("alienvault");
        pulse.Tags.Should().BeEquivalentTo(["emotet", "malware"]);
        pulse.IndicatorCount.Should().Be(3);
        pulse.Created.Should().Be(DateTimeOffset.Parse("2026-09-20T12:00:00Z"));
        pulse.PulseUrl.Should().Be("https://otx.alienvault.com/pulse/abc123");
    }

    [Fact]
    public async Task GetPulsesAsync_ShouldSkip_RatherThanThrow_ForAPulseMissingAName()
    {
        var service = CreateService("TEST_KEY", _ => JsonResponse(SampleJson), out _);

        var result = await service.GetPulsesAsync(null);

        result.Select(p => p.Id).Should().NotContain("missing-name");
    }

    [Fact]
    public async Task GetPulsesAsync_ShouldRequestSubscribed_WhenNoSearchTermGiven()
    {
        HttpRequestMessage? captured = null;
        var service = CreateService("TEST_KEY", request =>
        {
            captured = request;
            return JsonResponse(SampleJson);
        }, out _);

        await service.GetPulsesAsync(null);

        captured!.RequestUri!.ToString().Should().Contain("pulses/subscribed");
    }

    [Fact]
    public async Task GetPulsesAsync_ShouldRequestSearch_WhenASearchTermIsGiven()
    {
        HttpRequestMessage? captured = null;
        var service = CreateService("TEST_KEY", request =>
        {
            captured = request;
            return JsonResponse(SampleJson);
        }, out _);

        await service.GetPulsesAsync("emotet");

        captured!.RequestUri!.ToString().Should().Contain("search/pulses").And.Contain("q=emotet");
    }

    [Fact]
    public async Task GetPulsesAsync_ShouldSendTheApiKeyHeader()
    {
        HttpRequestMessage? captured = null;
        var service = CreateService("TEST_KEY", request =>
        {
            captured = request;
            return JsonResponse(SampleJson);
        }, out _);

        await service.GetPulsesAsync(null);

        captured!.Headers.GetValues("X-OTX-API-KEY").Should().ContainSingle("TEST_KEY");
    }

    [Fact]
    public async Task GetPulsesAsync_ShouldReturnEmpty_RatherThanThrow_WhenTheApiCallFails()
    {
        var service = CreateService("TEST_KEY", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized), out _);

        var result = await service.GetPulsesAsync(null);

        result.Should().BeEmpty();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static OtxThreatIntelService CreateService(
        string apiKey,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        out Func<int> requestCount)
    {
        var count = 0;
        var handler = new RecordingHandler(request =>
        {
            count++;
            return responseFactory(request);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://otx.alienvault.com/api/v1/") };
        requestCount = () => count;

        return new OtxThreatIntelService(
            http,
            Options.Create(new ThreatIntelOptions { OtxApiKey = apiKey }),
            NullLogger<OtxThreatIntelService>.Instance);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
