using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;

namespace GwsBusinessSuite.Tests;

public sealed class AutomationAgentNodeTests
{
    private static readonly JsonElement EmptyInput = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task ExecuteAgentAsync_ShouldCallAnAllowedTool_ThenFinish()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, """{"statusCode":200,"body":"ok"}""", new Dictionary<string, string>()));
        var ollama = new ScriptedOllamaService(
        [
            new OllamaChatResponse("", [new OllamaToolCall("core.httpRequest", """{"method":"GET","url":"https://example.test"}""")]),
            new OllamaChatResponse("Fetched the page successfully.", [])
        ]);
        var registry = new AutomationNodeRegistry(httpClient, ollama);
        var node = NewAgentNode("""{"model":"qwen2.5-coder","goal":"Fetch the homepage","allowedTools":["core.httpRequest"],"maxSteps":5,"outputField":"agentResult"}""");

        var result = await registry.ExecuteAsync(node, EmptyInput, credentialJson: null);

        httpClient.Requests.Should().ContainSingle();
        var agentResult = result.Outputs["main"][0].GetProperty("agentResult");
        agentResult.GetProperty("finished").GetBoolean().Should().BeTrue();
        agentResult.GetProperty("finalAnswer").GetString().Should().Be("Fetched the page successfully.");
        agentResult.GetProperty("stepCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAgentAsync_ShouldRejectATool_NotInTheAllowlist_WithoutExecutingIt()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>()));
        var ollama = new ScriptedOllamaService(
        [
            new OllamaChatResponse("", [new OllamaToolCall("crm.saveContact", "{}")]),
            new OllamaChatResponse("I can't do that, stopping.", [])
        ]);
        var registry = new AutomationNodeRegistry(httpClient, ollama);
        var node = NewAgentNode("""{"model":"qwen2.5-coder","goal":"Do a thing","allowedTools":["core.httpRequest"],"maxSteps":5}""");

        var result = await registry.ExecuteAsync(node, EmptyInput, credentialJson: null);

        httpClient.Requests.Should().BeEmpty("the requested tool was never in the allowlist");
        var agentResult = result.Outputs["main"][0].GetProperty("agentResult");
        var steps = agentResult.GetProperty("steps").EnumerateArray().ToList();
        steps[0].GetProperty("rejected").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("core.wait")]
    [InlineData("ai.agent")]
    [InlineData("core.webhookTrigger")]
    public async Task ExecuteAgentAsync_ShouldThrow_WhenAllowedToolsIncludesAForbiddenType(string forbiddenTool)
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>()));
        var ollama = new ScriptedOllamaService([]);
        var registry = new AutomationNodeRegistry(httpClient, ollama);
        var node = NewAgentNode($$"""{"model":"qwen2.5-coder","goal":"x","allowedTools":["{{forbiddenTool}}"],"maxSteps":5}""");

        var act = () => registry.ExecuteAsync(node, EmptyInput, credentialJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        ollama.CallCount.Should().Be(0, "validation must happen before the first model call");
    }

    [Fact]
    public async Task ExecuteAgentAsync_ShouldThrow_ForAnUnknownTool()
    {
        var registry = new AutomationNodeRegistry(new RecordingHttpClient(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>())), new ScriptedOllamaService([]));
        var node = NewAgentNode("""{"model":"qwen2.5-coder","goal":"x","allowedTools":["not.a.real.tool"],"maxSteps":5}""");

        var act = () => registry.ExecuteAsync(node, EmptyInput, credentialJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAgentAsync_ShouldStopAtMaxSteps_WhenTheModelNeverFinishes()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>()));
        var ollama = new ScriptedOllamaService(Enumerable.Range(0, 20)
            .Select(_ => new OllamaChatResponse("", new[] { new OllamaToolCall("core.httpRequest", """{"method":"GET","url":"https://example.test"}""") }))
            .ToList());
        var registry = new AutomationNodeRegistry(httpClient, ollama);
        var node = NewAgentNode("""{"model":"qwen2.5-coder","goal":"loop forever","allowedTools":["core.httpRequest"],"maxSteps":3}""");

        var result = await registry.ExecuteAsync(node, EmptyInput, credentialJson: null);

        var agentResult = result.Outputs["main"][0].GetProperty("agentResult");
        agentResult.GetProperty("finished").GetBoolean().Should().BeFalse();
        agentResult.GetProperty("stepCount").GetInt32().Should().Be(3);
        httpClient.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExecuteAgentAsync_ShouldRespectTheHardStepCeiling_EvenIfConfiguredHigher()
    {
        var httpClient = new RecordingHttpClient(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>()));
        var ollama = new ScriptedOllamaService(Enumerable.Range(0, 50)
            .Select(_ => new OllamaChatResponse("", new[] { new OllamaToolCall("core.httpRequest", """{"method":"GET","url":"https://example.test"}""") }))
            .ToList());
        var registry = new AutomationNodeRegistry(httpClient, ollama);
        var node = NewAgentNode("""{"model":"qwen2.5-coder","goal":"loop forever","allowedTools":["core.httpRequest"],"maxSteps":1000}""");

        var result = await registry.ExecuteAsync(node, EmptyInput, credentialJson: null);

        result.Outputs["main"][0].GetProperty("agentResult").GetProperty("stepCount").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task ExecuteAgentAsync_ShouldThrow_WhenGoalIsMissing()
    {
        var registry = new AutomationNodeRegistry(new RecordingHttpClient(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>())), new ScriptedOllamaService([]));
        var node = NewAgentNode("""{"model":"qwen2.5-coder","goal":"","allowedTools":["core.httpRequest"]}""");

        var act = () => registry.ExecuteAsync(node, EmptyInput, credentialJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAgentAsync_ShouldThrow_WhenNoToolsAreAllowed()
    {
        var registry = new AutomationNodeRegistry(new RecordingHttpClient(new AutomationHttpResponse(200, "{}", new Dictionary<string, string>())), new ScriptedOllamaService([]));
        var node = NewAgentNode("""{"model":"qwen2.5-coder","goal":"x","allowedTools":[]}""");

        var act = () => registry.ExecuteAsync(node, EmptyInput, credentialJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static AutomationNodeSnapshot NewAgentNode(string parametersJson) => new(
        Guid.NewGuid(), "AI Agent", "ai.agent", 1, parametersJson, null, false, false, false, 1, 0, 0);

    private sealed class RecordingHttpClient(AutomationHttpResponse response) : IAutomationHttpClient
    {
        public List<AutomationHttpRequest> Requests { get; } = [];

        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }

    private sealed class ScriptedOllamaService(IReadOnlyList<OllamaChatResponse> responses) : IOllamaService
    {
        private int _index;
        public int CallCount { get; private set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException("The agent node only uses ChatAsync.");

        public Task<OllamaChatResponse> ChatAsync(
            string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition>? tools = null, CancellationToken ct = default)
        {
            CallCount++;
            if (_index >= responses.Count)
                throw new InvalidOperationException("ScriptedOllamaService ran out of scripted responses.");
            return Task.FromResult(responses[_index++]);
        }

        public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task PullModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
