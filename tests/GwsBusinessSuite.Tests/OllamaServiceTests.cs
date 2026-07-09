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

    private static OllamaService CreateService(HttpMessageHandler handler, ILogger<OllamaService> logger)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        return new OllamaService(client, logger);
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
