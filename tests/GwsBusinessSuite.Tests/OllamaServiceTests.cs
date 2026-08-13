using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Tests;

public sealed class OllamaServiceTests
{
    [Fact]
    public async Task GenerateAsync_ShouldLogWarningAndRethrow_OnNonSuccessStatus()
    {
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, logger);

        var action = async () => await service.GenerateAsync("llama3", "system", "prompt");

        await action.Should().ThrowAsync<HttpRequestException>();
        logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task GenerateAsync_ShouldLogWarningAndRethrow_OnTimeout()
    {
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(_ => throw new TaskCanceledException("Simulated timeout."));
        var service = CreateService(handler, logger);

        var action = async () => await service.GenerateAsync("llama3", "system", "prompt");

        await action.Should().ThrowAsync<TaskCanceledException>();
        logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task ListModelsAsync_ShouldLogWarningAndRethrow_OnMalformedJson()
    {
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not valid json")
        });
        var service = CreateService(handler, logger);

        var action = async () => await service.ListModelsAsync();

        await action.Should().ThrowAsync<System.Text.Json.JsonException>();
        logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task ListModelsAsync_ShouldReturnModelNames_OnSuccess()
    {
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"models":[{"name":"llama3"},{"name":"mistral"}]}""")
        });
        var service = CreateService(handler, logger);

        var models = await service.ListModelsAsync();

        models.Should().BeEquivalentTo(["llama3", "mistral"]);
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task WarmModelAsync_ShouldSendAnEmptyKeepAliveRequest()
    {
        string? payload = null;
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"done":true}""")
            };
        });
        var service = CreateService(handler, logger);

        await service.WarmModelAsync("sentinelgpt");

        payload.Should().Contain("\"model\":\"sentinelgpt\"");
        payload.Should().Contain("\"keep_alive\":\"30m\"");
        payload.Should().NotContain("\"prompt\"");
    }

    [Fact]
    public async Task GenerateStreamAsync_ShouldCaptureInteractiveChatPerformanceWithoutPromptContent()
    {
        string? payload = null;
        var tracker = new OllamaPerformanceTracker();
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                """
                {"response":"ok","done":false}
                {"response":"","done":true,"load_duration":1000000000,"prompt_eval_count":10,"prompt_eval_duration":500000000,"eval_count":20,"eval_duration":1000000000}
                """)
            };
        });
        var service = CreateService(handler, logger, tracker);

        await foreach (var _ in service.GenerateStreamAsync(
            "sentinelgpt:latest", "system", "private prompt text", 768))
        {
        }

        var snapshot = tracker.GetLatest("sentinelgpt");
        snapshot.Should().NotBeNull();
        snapshot!.Model.Should().Be("sentinelgpt:latest");
        snapshot.LoadMilliseconds.Should().Be(1_000);
        snapshot.PromptTokens.Should().Be(10);
        snapshot.OutputTokens.Should().Be(20);
        snapshot.TokensPerSecond.Should().Be(20);
        snapshot.ToString().Should().NotContain("private prompt text");
        payload.Should().Contain("\"options\":{\"num_predict\":768}");
    }

    [Fact]
    public async Task GenerateAsync_WithNumCtx_ShouldIncludeItInTheRequestOptions()
    {
        string? payload = null;
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"response":"ok","done":true}""")
            };
        });
        var service = CreateService(handler, logger);

        await service.GenerateAsync("qwen2.5-coder", "system", "prompt", numCtx: 8192);

        payload.Should().Contain("\"options\":{\"num_ctx\":8192}");
    }

    [Fact]
    public async Task GenerateAsync_WithoutNumCtx_ShouldNotIncludeAnOptionsBlock()
    {
        string? payload = null;
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"response":"ok","done":true}""")
            };
        });
        var service = CreateService(handler, logger);

        await service.GenerateAsync("sentinelgpt", "system", "prompt");

        payload.Should().NotContain("options");
    }

    [Fact]
    public async Task GenerateStreamAsync_ShouldNotCaptureBackgroundPerformance()
    {
        var scheduler = new OllamaWorkloadScheduler();
        var tracker = new OllamaPerformanceTracker();
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"response":"ok","done":true}""")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var service = new OllamaService(client, scheduler, tracker, logger);

        using (scheduler.UseBackgroundPriority())
        {
            await foreach (var _ in service.GenerateStreamAsync(
                "sentinelgpt", "system", "scheduled work"))
            {
            }
        }

        tracker.GetLatest("sentinelgpt").Should().BeNull();
    }

    private static OllamaService CreateService(
        HttpMessageHandler handler,
        ILogger<OllamaService> logger,
        OllamaPerformanceTracker? tracker = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        return new OllamaService(
            client,
            new OllamaWorkloadScheduler(),
            tracker ?? new OllamaPerformanceTracker(),
            logger);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    // Hand-written fake rather than a mocking library (none is referenced by this test
    // project) - just enough of ILogger<T> to assert a warning was recorded.
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
