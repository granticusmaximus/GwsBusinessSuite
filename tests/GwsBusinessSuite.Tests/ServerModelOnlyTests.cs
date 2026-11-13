using System.Runtime.CompilerServices;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Web.Services;
using Microsoft.JSInterop;

namespace GwsBusinessSuite.Tests;

// The server's Ollama as SentinelGPT's only model (13-11-26, Grant): with ServerAi:SingleSource and a
// server model, no panel uses a model on the browser's computer; local-only panels use the server's.
public sealed class ServerModelOnlyTests
{
    [Theory]
    [InlineData(ServerAiMode.Full, true, true)]
    [InlineData(ServerAiMode.Light, true, true)]
    [InlineData(ServerAiMode.None, true, false)]
    [InlineData(ServerAiMode.Full, false, false)]
    public void ServerModelOnly_ShouldNeedASingleSourceAndAServerModel(ServerAiMode mode, bool singleSource, bool expected) =>
        new ServerAiOptions { Mode = mode, SingleSource = singleSource }.ServerModelOnly.Should().Be(expected);

    [Fact]
    public void LocalCompute_ShouldBeOffWhenTheServersModelIsTheOnlySource()
    {
        new ServerAiOptions { Mode = ServerAiMode.Light }.UsesLocalCompute.Should().BeTrue();
        new ServerAiOptions { Mode = ServerAiMode.Light, SingleSource = true }.UsesLocalCompute.Should().BeFalse();
        new ServerAiOptions { Mode = ServerAiMode.None, SingleSource = true }.UsesLocalCompute.Should().BeTrue("with no server model the Mac is all there is");
    }

    [Fact]
    public async Task APanelWithAServerPath_ShouldFindNoLocalModel_WithoutTouchingTheBrowser()
    {
        var local = BrowserLocalOllamaService.For(new NoBrowser(), new ServerAiOptions { Mode = ServerAiMode.Full, SingleSource = true });

        var status = await local.ProbeAsync();

        status.IsReachable.Should().BeFalse();
        status.Error.Should().Be(BrowserLocalOllamaService.ServerModelOnlyMessage);
    }

    [Fact]
    public async Task ALocalOnlyPanel_ShouldRunOnTheServersModel()
    {
        var server = new ServerModel();
        var local = BrowserLocalOllamaService.For(new NoBrowser(), new ServerAiOptions { Mode = ServerAiMode.Full, SingleSource = true }, server);

        (await local.ProbeAsync()).Models.Should().Equal("sentinelgpt:latest", "embeddinggemma:latest");
        (await local.GenerateAsync("sentinelgpt", "system", "hello")).Should().Be("server: hello");
        (await local.GenerateJsonAsync("sentinelgpt", "system", "steps")).Should().Be("server: steps");
        server.LastSystem.Should().Contain("JSON only");
        var streamed = new List<string>();
        await foreach (var piece in local.GenerateStreamAsync("sentinelgpt", "system", "stream")) streamed.Add(piece);
        streamed.Should().Equal("server: stream");
        await FluentActions.Awaiting(() => local.GenerateWithImagesAsync("llava", "s", "u", ["aGk="], false)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task WithoutSingleSource_ThePanelsServerModelShouldBeIgnored()
    {
        var local = BrowserLocalOllamaService.For(new NoBrowser(), new ServerAiOptions { Mode = ServerAiMode.Full }, new ServerModel());

        local.Off.Should().BeFalse();
        local.Server.Should().BeNull();
        await FluentActions.Awaiting(() => local.ProbeAsync()).Should().ThrowAsync<InvalidOperationException>("it asks the browser, as before");
    }

    private sealed class NoBrowser : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("The browser was asked.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("The browser was asked.");
    }

    private sealed class ServerModel : IOllamaService
    {
        public string? LastSystem { get; private set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastSystem = systemPrompt;
            return Task.FromResult("server: " + userPrompt);
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return "server: " + userPrompt;
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(["sentinelgpt:latest", "embeddinggemma:latest"]);
        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }
}
