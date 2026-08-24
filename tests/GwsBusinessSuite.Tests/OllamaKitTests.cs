using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.Tests;

public sealed class OllamaKitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gws-ollamakit-{Guid.NewGuid():N}");

    public OllamaKitTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ChatStreamAsync_YieldsContentDeltasThenADoneChunk()
    {
        var body = string.Join('\n',
            """{"message":{"content":"Hello"},"done":false}""",
            """{"message":{"content":" world"},"done":false}""",
            """{"message":{"content":""},"done":true}""");
        using var client = CreateClient(_ => NdjsonResponse(body));

        var chunks = new List<OllamaChatStreamChunk>();
        await foreach (var chunk in client.ChatStreamAsync(
            "llama3.2", [new OllamaChatMessage("user", "hi")], [], default))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(3);
        string.Concat(chunks.Take(2).Select(c => c.ContentDelta)).Should().Be("Hello world");
        chunks[^1].Done.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallingAgent_WithNoToolCalls_StreamsContentAndRecordsTheAssistantTurn()
    {
        var body = string.Join('\n',
            """{"message":{"content":"Paris"},"done":false}""",
            """{"message":{"content":" is the capital."},"done":false}""",
            """{"message":{"content":""},"done":true}""");
        using var client = CreateClient(_ => NdjsonResponse(body));
        var agent = new OllamaToolCallingAgent(client, new FakeToolExecutor([]), "llama3.2", "system prompt", maxRounds: 3);

        var events = new List<OllamaAgentEvent>();
        await foreach (var e in agent.RunTurnStreamingAsync("What is the capital of France?", default))
            events.Add(e);

        events.Should().OnlyContain(e => e.ToolActivity == null);
        string.Concat(events.Select(e => e.ContentDelta)).Should().Be("Paris is the capital.");
        agent.Messages.Should().HaveCount(3); // system, user, assistant
        agent.Messages[^1].Role.Should().Be("assistant");
        agent.Messages[^1].Content.Should().Be("Paris is the capital.");
    }

    [Fact]
    public async Task ToolCallingAgent_WithAToolCall_DispatchesToTheExecutorAndContinues()
    {
        var round = 0;
        using var client = CreateClient(_ =>
        {
            round++;
            return round == 1
                ? NdjsonResponse("""{"message":{"content":"","tool_calls":[{"type":"function","function":{"name":"search_wiki","arguments":{"query":"deploy"}}}]},"done":true}""")
                : NdjsonResponse(string.Join('\n',
                    """{"message":{"content":"Found it."},"done":false}""",
                    """{"message":{"content":""},"done":true}"""));
        });
        var executor = new FakeToolExecutor([new OllamaToolDefinition("search_wiki", "Search", """{"type":"object"}""")]);
        var agent = new OllamaToolCallingAgent(client, executor, "qwen2.5-coder", "system prompt", maxRounds: 3);

        var events = new List<OllamaAgentEvent>();
        await foreach (var e in agent.RunTurnStreamingAsync("How do I deploy?", default))
            events.Add(e);

        executor.Calls.Should().ContainSingle(call => call.Name == "search_wiki");
        events.Should().Contain(e => e.ToolActivity == "search_wiki");
        string.Concat(events.Select(e => e.ContentDelta)).Should().Be("Found it.");
        agent.Messages.Should().Contain(m => m.Role == "tool" && m.ToolName == "search_wiki");
        agent.Messages[^1].Content.Should().Be("Found it.");
    }

    [Fact]
    public async Task ConversationSessionStore_RoundTripsMessagesIncludingToolCalls()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var messages = new List<OllamaChatMessage>
        {
            new("system", "s"),
            new("user", "Read a.cs"),
            new("tool", "{}") { ToolName = "read_file" }
        };

        var path = await store.SaveAsync(null, "llama3.2", messages, default);
        var loaded = await store.LoadAsync(path, default);

        loaded.Should().NotBeNull();
        loaded!.Model.Should().Be("llama3.2");
        loaded.Messages.Should().HaveCount(3);
        loaded.Messages[2].ToolName.Should().Be("read_file");
        store.List().Should().ContainSingle(item => item.Path == path);
    }

    [Fact]
    public async Task ConversationSessionStore_SaveWithNoExistingPathAlwaysMintsANewFile()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var messages = new[] { new OllamaChatMessage("system", "s"), new OllamaChatMessage("user", "u") };

        var first = await store.SaveAsync(null, "llama3.2", messages, default);
        var second = await store.SaveAsync(null, "llama3.2", messages, default);

        first.Should().NotBe(second);
        store.List().Should().HaveCount(2);
    }

    [Fact]
    public async Task ConversationSessionStore_DeleteRemovesTheFile()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var path = await store.SaveAsync(null, "llama3.2", [new OllamaChatMessage("system", "s")], default);

        store.Delete(path);

        File.Exists(path).Should().BeFalse();
        store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovedMemoryStore_SurfacesAPreviouslyApprovedAnswerForARelatedQuestion()
    {
        var store = new ApprovedMemoryStore(Path.Combine(_root, "approved-memory.json"));
        await store.AppendAsync("How do I deploy the affiliate service?", "Push to main; the pipeline handles it.", default);

        var context = await store.BuildContextAsync("What's the deploy process for affiliate?", default);
        var unrelated = await store.BuildContextAsync("What's my favorite color?", default);

        context.Should().Contain("Push to main; the pipeline handles it.");
        unrelated.Should().BeEmpty();
    }

    private static OllamaClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHttpMessageHandler(request => Task.FromResult(respond(request)));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        return new OllamaClient(http.BaseAddress!, http);
    }

    private static HttpResponseMessage NdjsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson")
    };

    private sealed class FakeToolExecutor(IReadOnlyList<OllamaToolDefinition> definitions) : IOllamaToolExecutor
    {
        public List<OllamaToolCall> Calls { get; } = [];
        public IReadOnlyList<OllamaToolDefinition> Definitions => definitions;

        public Task<string> ExecuteAsync(OllamaToolCall call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            return Task.FromResult("""{"result":"ok"}""");
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
