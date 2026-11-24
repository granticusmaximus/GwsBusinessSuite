using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
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
    public async Task GenerateAsync_OnNonSuccessStatus_ShouldKeepOllamasErrorTextAndStatusCode()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":"model 'sentinelgpt' not found"}""")
        });
        var service = CreateService(handler, new RecordingLogger<OllamaService>());

        var action = async () => await service.GenerateAsync("sentinelgpt", "system", "prompt");

        var thrown = await action.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        thrown.Which.Message.Should().Contain("404").And.Contain("model 'sentinelgpt' not found");
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
        payload.Should().Contain("\"keep_alive\":\"5m\"");
        payload.Should().NotContain("\"prompt\"");
    }

    [Fact]
    public async Task ChatAsync_ShouldSendTunedContextWindowTemperatureAndThinkFalse()
    {
        // Regression guard for a real bug: this payload previously had none of these tuned,
        // unlike GwsBusinessSuite.OllamaKit.OllamaClient (the CLI/Mac app's client), which left
        // every /api/chat tool-calling caller on Ollama's stock context window (often small
        // enough to silently truncate a multi-turn tool transcript) and a chat-tuned temperature
        // more prone to hallucinated tool arguments.
        string? payload = null;
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"content":"ok"},"done":true}""")
            };
        });
        var service = CreateService(handler, logger);

        await service.ChatAsync("sentinelgpt", [new OllamaChatMessage("user", "hi")]);

        payload.Should().Contain("\"options\":{\"num_ctx\":16384,\"temperature\":0.2}");
        payload.Should().Contain("\"think\":false");
    }

    [Fact]
    public async Task ChatAsync_WithImages_ShouldIncludeAPerMessageImagesArray()
    {
        string? payload = null;
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"content":"a quiet street"},"done":true}""")
            };
        });
        var service = CreateService(handler, logger);

        await service.ChatAsync("llava", [new OllamaChatMessage("user", "Describe this image.", Images: ["ZmFrZQ=="])]);

        payload.Should().Contain("\"images\":[\"ZmFrZQ==\"]");
    }

    [Fact]
    public async Task ChatAsync_WithoutImages_ShouldNotIncludeAnImagesKey()
    {
        string? payload = null;
        var logger = new RecordingLogger<OllamaService>();
        var handler = new RecordingHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"content":"ok"},"done":true}""")
            };
        });
        var service = CreateService(handler, logger);

        await service.ChatAsync("sentinelgpt", [new OllamaChatMessage("user", "hi")]);

        payload.Should().NotContain("\"images\"");
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
    public async Task EmbedAsync_ShouldUseBatchEmbedEndpointAndReturnEveryVector()
    {
        string? path = null;
        string? payload = null;
        var handler = new RecordingHandler(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model":"embeddinggemma","embeddings":[[1.0,0.0],[0.5,0.5]]}""")
            };
        });
        var service = CreateService(handler, new RecordingLogger<OllamaService>());

        var vectors = await service.EmbedAsync("embeddinggemma", ["first", "second"]);

        path.Should().Be("/api/embed");
        payload.Should().Contain("\"model\":\"embeddinggemma\"");
        payload.Should().Contain("\"input\":[\"first\",\"second\"]");
        vectors.Should().HaveCount(2);
        vectors[1].Should().Equal(0.5f, 0.5f);
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

    [Fact]
    public async Task GenerateAsync_ShouldUseTheConfiguredModelResidency()
    {
        string? payload = null;
        var handler = new RecordingHandler(request =>
        {
            payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"response":"ok","done":true}""") };
        });
        var service = new OllamaService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") },
            new OllamaWorkloadScheduler(), new OllamaPerformanceTracker(), new RecordingLogger<OllamaService>(), modelKeepAlive: "5m");

        await service.GenerateAsync("sentinelgpt", "system", "prompt");

        payload.Should().Contain("\"keep_alive\":\"5m\"");
    }

    [Fact]
    public async Task Generate_ShouldUseTheServersDefaultModel_WhenTheChosenOneIsntInstalled()
    {
        var asked = new List<string>();
        var tagsAnswer = true;
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags")
                return tagsAnswer
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"models":[{"name":"sentinelgpt:latest"},{"name":"llama3.2:latest"}]}""") }
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            asked.Add(System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"response":"ok","done":true}""") };
        });
        OllamaService Service() => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") }, new OllamaWorkloadScheduler(),
            new OllamaPerformanceTracker(), new RecordingLogger<OllamaService>(),
            Microsoft.Extensions.Options.Options.Create(new GwsBusinessSuite.Application.ContentStudio.ContentStudioOptions { Model = "sentinelgpt" }));

        await Service().GenerateAsync("muse-glimmer:latest", "system", "prompt");
        await Service().GenerateAsync("llama3.2", "system", "prompt");
        tagsAnswer = false;
        await Service().GenerateAsync("muse-glimmer:latest", "system", "prompt");

        asked.Should().Equal("sentinelgpt", "llama3.2", "muse-glimmer:latest");
    }

    // 19-11-26: callers asked for 4096, 8192 and 16384, and Ollama reloads the model whenever the
    // context changes (18 reloads in 40 minutes on the AI droplet). With ContentStudio:ContextLength
    // every request asks for the same size.
    [Fact]
    public async Task EveryRequest_ShouldAskForThePinnedContext_SoOllamaNeverReloadsTheModel()
    {
        var contexts = new List<(string Path, int? NumCtx)>();
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tags")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"models":[{"name":"sentinelgpt:latest"}]}""") };
            using var body = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            contexts.Add((path, body.RootElement.TryGetProperty("options", out var o) && o.TryGetProperty("num_ctx", out var c) ? c.GetInt32() : null));
            var answer = path == "/api/chat"
                ? """{"message":{"role":"assistant","content":"ok"},"done":true}"""
                : body.RootElement.TryGetProperty("stream", out var s) && s.GetBoolean()
                    ? "{\"response\":\"ok\",\"done\":false}\n{\"done\":true}\n"
                    : """{"response":"ok","done":true}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) };
        });
        OllamaService Service(int? pinned) => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") }, new OllamaWorkloadScheduler(),
            new OllamaPerformanceTracker(), new RecordingLogger<OllamaService>(),
            Microsoft.Extensions.Options.Options.Create(new GwsBusinessSuite.Application.ContentStudio.ContentStudioOptions { Model = "sentinelgpt", ContextLength = pinned }));

        var pinned = Service(8192);
        await pinned.GenerateAsync("sentinelgpt", "system", "prompt");
        await pinned.GenerateAsync("sentinelgpt", "system", "prompt", 16_384);
        await pinned.GenerateAsync("sentinelgpt", "system", "prompt", 4096);
        await foreach (var _ in pinned.GenerateStreamAsync("sentinelgpt", "system", "prompt")) { }
        await foreach (var _ in pinned.GenerateStreamAsync("sentinelgpt", "system", "prompt", 200)) { }
        await pinned.WarmModelAsync("sentinelgpt");
        await pinned.ChatAsync("sentinelgpt", [new OllamaChatMessage("user", "hi")]);

        contexts.Should().HaveCount(7).And.OnlyContain(c => c.NumCtx == 8192);

        contexts.Clear();
        await Service(null).GenerateAsync("sentinelgpt", "system", "prompt", 16_384);
        await Service(null).ChatAsync("sentinelgpt", [new OllamaChatMessage("user", "hi")]);
        contexts.Select(c => c.NumCtx).Should().Equal([16_384, 16_384], "without the setting each caller's size is sent as before");
    }

    // 19-11-26: the Mac's sentinelgpt is built on gemma4, a thinking model. /api/generate without
    // think:false made it reason at length before every answer ("Say OK." took 21 s), so World News
    // summaries and briefings queued behind each other and timed out in the Mac app. Every
    // generate, streamed or not, now asks for no thinking (the droplet's llama3.2 accepts it too).
    [Fact]
    public async Task EveryGenerate_ShouldAskForNoThinking()
    {
        var thinks = new List<bool?>();
        var handler = new RecordingHandler(request =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            thinks.Add(body.RootElement.TryGetProperty("think", out var think) ? think.GetBoolean() : null);
            var answer = body.RootElement.TryGetProperty("stream", out var s) && s.GetBoolean()
                ? "{\"response\":\"ok\",\"done\":false}\n{\"done\":true}\n"
                : """{"response":"ok","done":true}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) };
        });
        var service = CreateService(handler, new RecordingLogger<OllamaService>());

        await service.GenerateAsync("sentinelgpt", "system", "prompt");
        await service.GenerateAsync("sentinelgpt", "system", "prompt", 8192);
        await foreach (var _ in service.GenerateStreamAsync("sentinelgpt", "system", "prompt")) { }
        await foreach (var _ in service.GenerateStreamAsync("sentinelgpt", "system", "prompt", 200)) { }

        thinks.Should().HaveCount(4).And.OnlyContain(t => t == false);
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
