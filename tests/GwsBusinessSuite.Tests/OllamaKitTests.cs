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
    public async Task ToolCallingAgent_WithAMalformedToolCallAttempt_GivesOneCorrectiveRoundInsteadOfShowingRawJson()
    {
        var round = 0;
        using var client = CreateClient(_ =>
        {
            round++;
            return round == 1
                // Wrong field name ("parameters" not "arguments") - the exact real failure mode
                // observed from a local model attempting a tool call outside native tool_calls.
                ? NdjsonResponse(string.Join('\n',
                    """{"message":{"content":"{\"name\":\"search_wiki\",\"parameters\":{\"query\":\"deploy\"}}"},"done":false}""",
                    """{"message":{"content":""},"done":true}"""))
                : NdjsonResponse(string.Join('\n',
                    """{"message":{"content":"Here you go."},"done":false}""",
                    """{"message":{"content":""},"done":true}"""));
        });
        var executor = new FakeToolExecutor([new OllamaToolDefinition("search_wiki", "Search", """{"type":"object"}""")]);
        var agent = new OllamaToolCallingAgent(client, executor, "qwen2.5-coder", "system prompt", maxRounds: 3);

        var events = new List<OllamaAgentEvent>();
        await foreach (var e in agent.RunTurnStreamingAsync("How do I deploy?", default))
            events.Add(e);

        executor.Calls.Should().BeEmpty("the malformed attempt never named a real, parseable tool call");
        events.Should().Contain(e => e.ToolActivity == "retrying");
        // The malformed JSON streamed as real Content events before it could be classified (that
        // can't be known until the round finishes) - what matters is that a ToolActivity event
        // came after it, which is the same "treat this as replaceable" signal a real tool call
        // uses, so a UI consumer resets its display rather than appending. Mirror that here:
        // only the events after the last ToolActivity should make up the real, final answer.
        var lastToolActivityIndex = events.FindLastIndex(e => e.ToolActivity is not null);
        lastToolActivityIndex.Should().BeGreaterThanOrEqualTo(0);
        string.Concat(events.Skip(lastToolActivityIndex + 1).Select(e => e.ContentDelta)).Should().Be("Here you go.");
        agent.Messages.Should().Contain(m => m.Role == "tool" && m.ToolName == "invalid_tool_call");
        agent.Messages[^1].Content.Should().Be("Here you go.");
    }

    [Theory]
    [InlineData("""{"name":"search_wiki","parameters":{"query":"x"}}""", true)]
    [InlineData("""{"name":"read_file","arguments":{"path":"x","bad":undefined}}""", true)]
    [InlineData("""The deploy process pushes to main and the pipeline handles it.""", false)]
    [InlineData("""{"ok":true}""", false)]
    [InlineData("", false)]
    // A model unsure enough to narrate around a call instead of just issuing it - "I don't have
    // direct access, so I'll use get_page: {json}" - used to slip past this check entirely
    // because the content didn't *start* with '{'. It's just as much a failed attempt as a bare
    // malformed object and needs the same corrective retry, not a pass-through as a real answer.
    [InlineData("""I don't have direct access, so I will use the "get_page" function: {"name":"get_page","parameters":{"pageId":"<your_page_id>"}}""", true)]
    // An unrelated JSON example in a genuine answer (no tool-call shape: no "arguments"/
    // "parameters" alongside "name") must not false-positive into an unnecessary retry.
    [InlineData("""Sure, here's an example config: {"name":"my-app","version":"1.0"} - nothing to do with tools.""", false)]
    public void LooksLikeFailedToolCallAttempt_RecognizesJsonShapedAttemptsOnly(string content, bool expected) =>
        OllamaToolCallParsing.LooksLikeFailedToolCallAttempt(content).Should().Be(expected);

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
    public async Task ConversationSessionStore_RoundTripsAWorkspaceScopedConversationsWorkspaceRoot()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var workspace = Path.Combine(_root, "repo-a");
        Directory.CreateDirectory(workspace);

        var path = await store.SaveAsync(
            null, "qwen2.5-coder", [new OllamaChatMessage("system", "s")], default, workspaceRoot: workspace);
        var loaded = await store.LoadAsync(path, default);

        loaded.Should().NotBeNull();
        loaded!.WorkspaceRoot.Should().Be(Path.GetFullPath(workspace));
    }

    [Fact]
    public async Task ConversationSessionStore_List_ExcludesWorkspaceScopedConversations()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var workspace = Path.Combine(_root, "repo-a");
        Directory.CreateDirectory(workspace);
        await store.SaveAsync(null, "llama3.2", [new OllamaChatMessage("user", "ordinary chat")], default);
        await store.SaveAsync(
            null, "qwen2.5-coder", [new OllamaChatMessage("user", "dev chat")], default, workspaceRoot: workspace);

        var ordinary = store.List();

        ordinary.Should().ContainSingle();
        ordinary[0].Conversation.Messages[0].Content.Should().Be("ordinary chat");
    }

    [Fact]
    public async Task ConversationSessionStore_ListForWorkspace_ReturnsOnlyThatWorkspacesConversationsAndNotOrdinaryChats()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var workspaceA = Path.Combine(_root, "repo-a");
        var workspaceB = Path.Combine(_root, "repo-b");
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);
        await store.SaveAsync(null, "llama3.2", [new OllamaChatMessage("user", "ordinary chat")], default);
        var pathA = await store.SaveAsync(
            null, "qwen2.5-coder", [new OllamaChatMessage("user", "repo a chat")], default, workspaceRoot: workspaceA);
        await store.SaveAsync(
            null, "qwen2.5-coder", [new OllamaChatMessage("user", "repo b chat")], default, workspaceRoot: workspaceB);

        var forWorkspaceA = store.ListForWorkspace(workspaceA);

        forWorkspaceA.Should().ContainSingle(item => item.Path == pathA);
        forWorkspaceA[0].Conversation.Messages[0].Content.Should().Be("repo a chat");
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

    [Fact]
    public async Task ApprovedMemoryStore_ShouldNotMatchSolelyOnTheWordYou()
    {
        // Regression test: "you" was missing from StopWords (only "your" was listed), so any
        // question containing "you" - true of nearly all natural phrasing - term-overlap-matched
        // any prior approved exchange whose own question/answer also happened to contain "you",
        // regardless of actual topic. Confirmed live 2026-08-27 in the native Mac app: "Are you
        // faster now?" pulled in a completely unrelated prior "Can you find my ... page?" answer.
        var store = new ApprovedMemoryStore(Path.Combine(_root, "approved-memory2.json"));
        await store.AppendAsync("Can you find my Q3 sales report?", "Q3 sales totaled $42,000 across all regions.", default);

        var unrelated = await store.BuildContextAsync("Are you feeling faster today?", default);

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
