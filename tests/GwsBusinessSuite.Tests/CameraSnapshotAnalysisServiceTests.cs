using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class CameraSnapshotAnalysisServiceTests
{
    private static readonly CameraFeed SnapshotCamera = new(
        "gdot-1", "I-85 Camera", 33.75, -84.39,
        "https://example.test/snapshot.jpg", CameraStreamKind.Snapshot,
        "GDOT", "https://511ga.org");

    private static readonly CameraFeed HlsCamera = SnapshotCamera with { StreamKind = CameraStreamKind.Hls };

    [Fact]
    public async Task AnalyzeAsync_ShouldReturnNotSupported_ForHlsCameras_WithoutAnyCalls()
    {
        var ollama = new ScriptedOllamaService();
        var service = CreateService(NeverCalledHandler(), ollama, visionModelOverride: "llava");

        var result = await service.AnalyzeAsync(HlsCamera);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Live video");
        ollama.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReturnNotConfigured_WhenNoVisionModelSet_WithoutAnyCalls()
    {
        var ollama = new ScriptedOllamaService();
        var service = CreateService(NeverCalledHandler(), ollama, visionModelOverride: null);

        var result = await service.AnalyzeAsync(SnapshotCamera);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("No vision model configured");
        ollama.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReturnTheModelsDescription_OnSuccess()
    {
        var imageBytes = new byte[] { 1, 2, 3, 4 };
        var ollama = new ScriptedOllamaService("light traffic, clear skies");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(imageBytes)
        });
        var service = CreateService(handler, ollama, visionModelOverride: "llava");

        var result = await service.AnalyzeAsync(SnapshotCamera);

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Be("light traffic, clear skies");
        ollama.CallCount.Should().Be(1);
        ollama.LastModel.Should().Be("llava");
        ollama.LastMessages.Should().ContainSingle();
        ollama.LastMessages![0].Images.Should().ContainSingle(img => img == Convert.ToBase64String(imageBytes));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldRejectOversizedSnapshots_BeforeCallingOllama()
    {
        var ollama = new ScriptedOllamaService("should not be used");
        var oversized = new byte[11 * 1024 * 1024];
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(oversized)
        });
        var service = CreateService(handler, ollama, visionModelOverride: "llava");

        var result = await service.AnalyzeAsync(SnapshotCamera);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("10 MB");
        ollama.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldReturnAFailureMessage_WhenOllamaThrows()
    {
        var ollama = new ScriptedOllamaService(throwOnCall: true);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        var service = CreateService(handler, ollama, visionModelOverride: "llava");

        var result = await service.AnalyzeAsync(SnapshotCamera);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Analysis failed");
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldRejectNonHttpsSnapshotUrls_WithoutAnyCalls()
    {
        var ollama = new ScriptedOllamaService();
        var service = CreateService(NeverCalledHandler(), ollama, visionModelOverride: "llava");
        var httpCamera = SnapshotCamera with { StreamUrl = "http://example.test/snapshot.jpg" };

        var result = await service.AnalyzeAsync(httpCamera);

        result.Succeeded.Should().BeFalse();
        ollama.CallCount.Should().Be(0);
    }

    private static RecordingHandler NeverCalledHandler() =>
        new(_ => throw new InvalidOperationException("HTTP should not have been called."));

    private static CameraSnapshotAnalysisService CreateService(HttpMessageHandler handler, IOllamaService ollama, string? visionModelOverride)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        return new CameraSnapshotAnalysisService(
            httpClient, ollama, new FakeSiteSettingsService(visionModelOverride), NullLogger<CameraSnapshotAnalysisService>.Instance);
    }

    private sealed class FakeSiteSettingsService(string? visionModelOverride) : ISiteSettingsService
    {
        public Task<SiteSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteSettingsView(12, null, null, null, null, null, 8, null, visionModelOverride));

        public Task SaveSettingsAsync(SiteSettingsView settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedOllamaService(string? responseText = null, bool throwOnCall = false) : IOllamaService
    {
        public int CallCount { get; private set; }
        public string? LastModel { get; private set; }
        public IReadOnlyList<OllamaChatMessage>? LastMessages { get; private set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PullModelAsync(string model, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteModelAsync(string model, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OllamaChatResponse> ChatAsync(
            string model,
            IReadOnlyList<OllamaChatMessage> messages,
            IReadOnlyList<OllamaToolDefinition>? tools = null,
            CancellationToken ct = default)
        {
            CallCount++;
            LastModel = model;
            LastMessages = messages;
            if (throwOnCall)
            {
                throw new HttpRequestException("Simulated Ollama failure.");
            }
            return Task.FromResult(new OllamaChatResponse(responseText ?? string.Empty, []));
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
