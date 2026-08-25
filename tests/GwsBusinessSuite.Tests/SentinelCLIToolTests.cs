using System.Net;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.SentinelAgentKit;
using GwsBusinessSuite.SentinelCLI;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelCLIToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gws-sentinel-cli-{Guid.NewGuid():N}");

    public SentinelCLIToolTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Parse_UsesCurrentDirectoryAndCodingModelByDefault()
    {
        var options = CliOptions.Parse([], _root);

        Assert.Equal(CliMode.Agent, options.Mode);
        Assert.Equal(Path.GetFullPath(_root), options.WorkspaceRoot);
        Assert.Equal("qwen2.5-coder", options.Model);
        Assert.Equal(new Uri("http://127.0.0.1:11434/"), options.OllamaBaseUri);
        Assert.True(options.IsInteractive);
    }

    [Fact]
    public void Parse_AcceptsParentWorkspaceOrSpecificRepository()
    {
        var repo = Path.Combine(_root, "repo-a");
        Directory.CreateDirectory(repo);

        var parent = CliOptions.Parse(["Analyze", "the", "repos"], _root);
        var specific = CliOptions.Parse(["-C", repo, "Fix", "the", "parser"], _root);

        Assert.Equal(_root, parent.WorkspaceRoot);
        Assert.Equal("Analyze the repos", parent.Prompt);
        Assert.Equal(repo, specific.WorkspaceRoot);
        Assert.Equal("Fix the parser", specific.Prompt);
    }

    [Fact]
    public void Parse_RejectsRemoteOllamaServers()
    {
        var action = () => CliOptions.Parse(["--ollama-url", "https://example.com", "review"], _root);

        Assert.Throws<CliUsageException>(action);
    }

    [Fact]
    public async Task OllamaClient_ParsesToolCallingResponseAndSendsTools()
    {
        string? requestBody = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"message":{"role":"assistant","content":"","tool_calls":[{"type":"function","function":{"index":0,"name":"read_file","arguments":{"path":"Program.cs"}}}]},"done":true}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        using var client = new OllamaClient(http.BaseAddress, http);

        var result = await client.ChatAsync(
            "qwen2.5-coder",
            [new OllamaChatMessage("user", "Read Program.cs")],
            [new OllamaToolDefinition("read_file", "Read", """{"type":"object"}""")],
            default);

        Assert.Single(result.ToolCalls);
        Assert.Equal("read_file", result.ToolCalls[0].Name);
        using var resultArguments = JsonDocument.Parse(result.ToolCalls[0].ArgumentsJson);
        Assert.Equal("Program.cs", resultArguments.RootElement.GetProperty("path").GetString());
        Assert.Contains("\"tools\"", requestBody);
        Assert.Contains("\"stream\":false", requestBody);
    }

    [Fact]
    public async Task RunTurnAsync_WithAMalformedToolCallAttempt_GivesOneCorrectiveRoundInsteadOfReturningRawJson()
    {
        var round = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            round++;
            // Round 1: wrong field name ("parameters" not "arguments") - the real failure mode
            // observed from a local model attempting a tool call outside native tool_calls.
            var body = round == 1
                ? """{"message":{"role":"assistant","content":"{\"name\":\"read_file\",\"parameters\":{\"path\":\"a.cs\"}}"},"done":true}"""
                : """{"message":{"role":"assistant","content":"Here you go."},"done":true}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        using var client = new OllamaClient(http.BaseAddress, http);
        var tools = new WorkspaceTools(_root, new FakeApproval(true), readOnly: true);
        var agent = new SentinelCodingAgent(client, tools, "qwen2.5-coder", maxRounds: 3);

        var result = await agent.RunTurnAsync("Read a.cs", default);

        Assert.Equal("Here you go.", result);
        Assert.Contains(agent.Messages, m => m.Role == "tool" && m.ToolName == "invalid_tool_call");
    }

    [Fact]
    public void ModelCatalog_MatchesTheGwsRuntimeSet()
    {
        Assert.Equal(["llama3.2", "qwen2.5-coder", "deepseek-r1", "embeddinggemma"], ModelCatalog.RequiredModels);
        Assert.Contains("FROM llama3.2", ModelCatalog.SentinelProfile);
        Assert.True(OllamaModelManager.HasModel(["sentinelgpt:latest"], "sentinelgpt"));
        Assert.False(OllamaModelManager.HasModel(["deepseek-r1:14b"], "deepseek-r1"));
    }

    [Fact]
    public void SuggestedFreeModels_AreWellFormedAndUnique()
    {
        var suggestions = ModelCatalog.SuggestedFreeModels;

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.Description));
        });
        Assert.Equal(
            suggestions.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            suggestions.Count);
        Assert.All(ModelCatalog.RequiredModels, required =>
            Assert.Contains(suggestions, item => string.Equals(item.Name, required, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task PullAsync_DoesNotShellOutWhenDeclined()
    {
        var approval = new FakeApproval(false);
        var manager = new OllamaModelManager(new OllamaClient(new Uri("http://127.0.0.1:11434/")), approval);

        var pulled = await manager.PullAsync("mistral", default);

        Assert.False(pulled);
        Assert.Single(approval.Requests);
        Assert.Equal("Ollama model download", approval.Requests[0].Action);
        Assert.Contains("mistral", approval.Requests[0].Details);
    }

    [Fact]
    public void AgentPersonas_FindIsCaseInsensitiveAndRejectsUnknownNames()
    {
        Assert.Equal(AgentPersonas.Reviewer, AgentPersonas.Find("REVIEWER"));
        Assert.Null(AgentPersonas.Find("does-not-exist"));
        Assert.Contains(AgentPersonas.Default, AgentPersonas.All);
    }

    [Fact]
    public void SkillLibrary_ListsLoadsAndRejectsPathEscapingNames()
    {
        var skillsDir = Path.Combine(_root, "skills");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(skillsDir, "commit-messages.md"), "Write conventional commit messages.");
        var library = new SkillLibrary(skillsDir);

        Assert.Equal(["commit-messages"], library.List());
        Assert.Equal("Write conventional commit messages.", library.Load("commit-messages"));
        Assert.Null(library.Load("does-not-exist"));
        Assert.Null(library.Load("../outside"));
        Assert.Null(library.Load("nested/escape"));
    }

    [Fact]
    public void ContentToolFallback_RecognizesOnlyRegisteredStrictJsonActions()
    {
        var definitions = new[]
        {
            new OllamaToolDefinition("read_file", "Read", """{"type":"object"}""")
        };

        var calls = SentinelCodingAgent.TryParseContentToolCall(
            """{"name":"read_file","arguments":{"path":"Program.cs"}}""",
            definitions);
        var unknown = SentinelCodingAgent.TryParseContentToolCall(
            """{"name":"delete_everything","arguments":{}}""",
            definitions);

        Assert.Single(calls);
        using var callArguments = JsonDocument.Parse(calls[0].ArgumentsJson);
        Assert.Equal("Program.cs", callArguments.RootElement.GetProperty("path").GetString());
        Assert.Empty(unknown);
    }

    [Fact]
    public void ContentToolFallback_RecognizesAnAllJsonRegisteredBatch()
    {
        var definitions = new[]
        {
            new OllamaToolDefinition("read_file", "Read", """{"type":"object"}"""),
            new OllamaToolDefinition("replace_in_file", "Replace", """{"type":"object"}""")
        };
        var content = """
            {"name":"read_file","arguments":{"path":"sample.txt"}}
            {"name":"replace_in_file","arguments":{"path":"sample.txt","old_text":"a","new_text":"b"}}
            """;

        var calls = SentinelCodingAgent.TryParseContentToolCall(content, definitions);

        Assert.Equal(2, calls.Count);
        Assert.Equal("read_file", calls[0].Name);
        Assert.Equal("replace_in_file", calls[1].Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeApproval(bool approved) : IUserApproval
    {
        public List<(string Action, string Details)> Requests { get; } = [];

        public Task<bool> ConfirmAsync(string action, string details, CancellationToken cancellationToken)
        {
            Requests.Add((action, details));
            return Task.FromResult(approved);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
