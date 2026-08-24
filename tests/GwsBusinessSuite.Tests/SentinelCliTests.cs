using System.Net;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.SentinelCli;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gws-sentinel-cli-{Guid.NewGuid():N}");

    public SentinelCliTests() => Directory.CreateDirectory(_root);

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
    public async Task ReadAndSearch_AreBoundedToNonSecretWorkspaceFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Widget.cs"), "line one\npublic class Widget {}\n");
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "SECRET=value\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "appsettings.Development.local.json"), "{\"ApiKey\":\"secret\"}\n");
        var tools = new WorkspaceTools(_root, new FakeApproval(true), readOnly: false);

        var read = await tools.ExecuteAsync(Call("read_file", new { path = "Widget.cs", start_line = 2 }), default);
        var search = await tools.ExecuteAsync(Call("search_text", new { query = "Widget", glob = "*.cs" }), default);
        var secret = await tools.ExecuteAsync(Call("read_file", new { path = ".env" }), default);
        var localSettings = await tools.ExecuteAsync(
            Call("read_file", new { path = "appsettings.Development.local.json" }), default);
        var escape = await tools.ExecuteAsync(Call("read_file", new { path = "../outside.txt" }), default);

        Assert.Contains("public class Widget", read);
        Assert.Contains("Widget.cs", search);
        Assert.Contains("secret or credential", secret, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret or credential", localSettings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("escapes the workspace", escape, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeWorkspace_DetectsImmediateRepositoryDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo-a", ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "repo-b", ".git"));
        var tools = new WorkspaceTools(_root, new FakeApproval(true), readOnly: true);

        var description = tools.DescribeWorkspace();

        Assert.Contains("repo-a", description);
        Assert.Contains("repo-b", description);
    }

    [Fact]
    public async Task ReplaceInFile_RequiresApprovalAndPerformsExactEdit()
    {
        var path = Path.Combine(_root, "sample.txt");
        await File.WriteAllTextAsync(path, "before\n");
        var approval = new FakeApproval(true);
        var tools = new WorkspaceTools(_root, approval, readOnly: false);

        var result = await tools.ExecuteAsync(
            Call("replace_in_file", new { path = "sample.txt", old_text = "before", new_text = "after" }),
            default);

        Assert.Contains("\"changed\":true", result);
        Assert.Equal("after\n", await File.ReadAllTextAsync(path));
        Assert.Single(approval.Requests);
    }

    [Fact]
    public async Task ReplaceInFile_DoesNotChangeFileWhenDeclined()
    {
        var path = Path.Combine(_root, "sample.txt");
        await File.WriteAllTextAsync(path, "before\n");
        var tools = new WorkspaceTools(_root, new FakeApproval(false), readOnly: false);

        var result = await tools.ExecuteAsync(
            Call("replace_in_file", new { path = "sample.txt", old_text = "before", new_text = "after" }),
            default);

        Assert.Contains("\"approved\":false", result);
        Assert.Equal("before\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void ReadOnlyMode_DoesNotExposeMutationTools()
    {
        var tools = new WorkspaceTools(_root, new FakeApproval(true), readOnly: true);

        Assert.DoesNotContain(tools.Definitions, definition => definition.Name is "replace_in_file" or "write_file" or "run_command");
    }

    [Fact]
    public async Task RunCommand_RejectsMutatingGitCommands()
    {
        var approval = new FakeApproval(true);
        var tools = new WorkspaceTools(_root, approval, readOnly: false);

        var result = await tools.ExecuteAsync(
            Call("run_command", new { program = "git", arguments = new[] { "reset", "--hard" } }),
            default);

        Assert.Contains("read-only git commands", result);
        Assert.Empty(approval.Requests);
    }

    [Theory]
    [InlineData("node", "-e", "console.log(process.env)")]
    [InlineData("python3", "-c", "print('unsafe')")]
    [InlineData("find", ".", "-delete")]
    [InlineData("sed", "-i", "s/a/b/", "file.txt")]
    public async Task RunCommand_RejectsExecutionAndMutationBypasses(string program, params string[] arguments)
    {
        var approval = new FakeApproval(true);
        var tools = new WorkspaceTools(_root, approval, readOnly: false);

        var result = await tools.ExecuteAsync(Call("run_command", new { program, arguments }), default);

        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(approval.Requests);
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
    public void EffectiveReadOnly_ComposesPermanentReadOnlyAndPlanMode()
    {
        var permanentlyReadOnly = new WorkspaceTools(_root, new FakeApproval(true), readOnly: true);
        var normally = new WorkspaceTools(_root, new FakeApproval(true), readOnly: false);

        permanentlyReadOnly.SetPlanMode(true);
        permanentlyReadOnly.SetPlanMode(false);
        Assert.True(permanentlyReadOnly.EffectiveReadOnly, "--read-only must survive /act.");

        Assert.False(normally.EffectiveReadOnly);
        normally.SetPlanMode(true);
        Assert.True(normally.EffectiveReadOnly);
        normally.SetPlanMode(false);
        Assert.False(normally.EffectiveReadOnly);
    }

    [Fact]
    public async Task UnreachableApproval_IsNeverInvokedWhenEffectiveReadOnlyIsTrue()
    {
        var tools = new WorkspaceTools(_root, new UnreachableApproval(), readOnly: true);

        var result = await tools.ExecuteAsync(
            Call("replace_in_file", new { path = "sample.txt", old_text = "a", new_text = "b" }), default);

        Assert.Contains("Unknown or disabled tool", result);
    }

    [Fact]
    public void AgentPersonas_FindIsCaseInsensitiveAndRejectsUnknownNames()
    {
        Assert.Equal(AgentPersonas.Reviewer, AgentPersonas.Find("REVIEWER"));
        Assert.Null(AgentPersonas.Find("does-not-exist"));
        Assert.Contains(AgentPersonas.Default, AgentPersonas.All);
    }

    [Fact]
    public async Task SessionStore_RoundTripsAConversationIncludingToolCalls()
    {
        var store = new SessionStore(Path.Combine(_root, "sessions"));
        var toolCallMessage = new OllamaChatMessage("assistant", "")
        {
            ToolCalls = [new OllamaApiToolCall { Function = new OllamaApiFunctionCall { Name = "read_file", Arguments = JsonDocument.Parse("""{"path":"a.cs"}""").RootElement } }]
        };
        var messages = new List<OllamaChatMessage>
        {
            new("system", "You are SentinelGPT Code."),
            new("user", "Read a.cs"),
            toolCallMessage,
            new("tool", "{}") { ToolName = "read_file" }
        };

        var path = await store.SaveAsync(null, _root, "qwen2.5-coder", messages, default);
        var loaded = await store.LoadAsync(path, default);

        Assert.NotNull(loaded);
        Assert.Equal("qwen2.5-coder", loaded.Model);
        Assert.Equal(4, loaded.Messages.Count);
        Assert.Equal("read_file", loaded.Messages[2].ToolCalls?[0].Function.Name);
        Assert.Equal("a.cs", loaded.Messages[2].ToolCalls?[0].Function.Arguments.GetProperty("path").GetString());
        Assert.Single(store.ListForWorkspace(_root));
    }

    [Fact]
    public async Task SessionStore_SaveWithNoExistingPathAlwaysMintsANewFile()
    {
        // The invariant /clear's currentSessionPath=null reset relies on: passing null for
        // existingPath must never reuse a prior file, even for the same workspace back-to-back.
        var store = new SessionStore(Path.Combine(_root, "sessions"));
        var messages = new[] { new OllamaChatMessage("system", "s"), new OllamaChatMessage("user", "u") };

        var first = await store.SaveAsync(null, _root, "qwen2.5-coder", messages, default);
        var second = await store.SaveAsync(null, _root, "qwen2.5-coder", messages, default);

        Assert.NotEqual(first, second);
        Assert.Equal(2, store.ListForWorkspace(_root).Count);
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

    private static OllamaToolCall Call(string name, object arguments) =>
        new(name, JsonSerializer.Serialize(arguments));

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
