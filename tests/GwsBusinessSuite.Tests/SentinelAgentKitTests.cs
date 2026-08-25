using System.Text.Json;
using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.SentinelAgentKit;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelAgentKitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gws-sentinel-agent-kit-{Guid.NewGuid():N}");

    public SentinelAgentKitTests() => Directory.CreateDirectory(_root);

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
    public async Task AllowRunCommandFalse_HidesTheToolButStillExposesFileEdits()
    {
        // The native Mac app's App Sandbox denies Process.Start outright (confirmed empirically),
        // so it constructs WorkspaceTools with allowRunCommand: false. Everything else - read,
        // search, and approval-gated edits - must keep working.
        var approval = new FakeApproval(true);
        var tools = new WorkspaceTools(_root, approval, readOnly: false, allowRunCommand: false);

        Assert.DoesNotContain(tools.Definitions, definition => definition.Name == "run_command");
        var result = await tools.ExecuteAsync(
            Call("run_command", new { program = "git", arguments = new[] { "status" } }), default);
        Assert.Contains("Unknown or disabled tool", result);
        Assert.Empty(approval.Requests);

        Assert.Contains(tools.Definitions, definition => definition.Name == "replace_in_file");
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
}
