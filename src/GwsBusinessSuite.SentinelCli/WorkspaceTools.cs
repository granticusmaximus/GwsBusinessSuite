using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.SentinelCli;

public interface IUserApproval
{
    Task<bool> ConfirmAsync(string action, string details, CancellationToken cancellationToken);
}

public sealed class ConsoleUserApproval(bool autoApprove) : IUserApproval
{
    public Task<bool> ConfirmAsync(string action, string details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine();
        Console.WriteLine($"Proposed {action}:");
        Console.WriteLine(details);
        if (autoApprove)
        {
            Console.WriteLine("Approved by --yes.");
            return Task.FromResult(true);
        }
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("Declined because confirmation requires an interactive terminal.");
            return Task.FromResult(false);
        }
        Console.Write("Apply? [y/N] ");
        var answer = Console.ReadLine();
        return Task.FromResult(string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase));
    }
}

// /fleet's WorkspaceTools instance is always readOnly, which already keeps every mutating tool
// out of Definitions and out of ExecuteAsync's dispatch (see the "when !EffectiveReadOnly" guards
// below) - approval should therefore be structurally unreachable for a fleet run. Using this
// instead of ConsoleUserApproval turns that from an assumption into an assertion: if a future
// change to those guards ever lets a mutation through in read-only mode, this throws loudly
// instead of silently prompting N concurrent agents against the same stdin.
public sealed class UnreachableApproval : IUserApproval
{
    public Task<bool> ConfirmAsync(string action, string details, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Approval was requested on a read-only WorkspaceTools instance.");
}

public sealed class WorkspaceTools
{
    private const int MaxFileBytes = 1_000_000;
    private const int MaxReadLines = 500;
    private const int MaxToolOutput = 45_000;
    private const int MaxWriteCharacters = 750_000;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".idea", ".vs", ".vscode", "node_modules", "bin", "obj",
        "dist", "build", "coverage", "TestResults", ".next", ".nuxt", ".cache", ".terraform"
    };

    private static readonly HashSet<string> SecretFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".npmrc", ".pypirc", "secrets.json", "credentials", "credentials.json",
        "id_rsa", "id_ed25519", "known_hosts", "authorized_keys", ".netrc", ".git-credentials",
        "auth.json", "launchSettings.json", "appsettings.Local.json"
    };

    private static readonly HashSet<string> AllowedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "git", "npm", "node", "rg", "fd", "find", "ls", "sed", "head",
        "tail", "wc", "bash", "sh", "python", "python3", "xcodebuild"
    };

    private static readonly HashSet<string> ReadOnlyGitCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "rev-parse", "branch", "ls-files", "grep", "describe",
        "tag"
    };

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly IUserApproval _approval;
    private readonly bool _readOnly;
    private readonly bool _quiet;
    private bool _planMode;

    public WorkspaceTools(string workspaceRoot, IUserApproval approval, bool readOnly, bool quiet = false)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _rootPrefix = _root + Path.DirectorySeparatorChar;
        _approval = approval;
        _readOnly = readOnly;
        _quiet = quiet;
    }

    public string Root => _root;

    // --read-only is permanent for the process; /plan is a session-local toggle layered on top so
    // switching back to /act restores whatever --read-only already required.
    public bool EffectiveReadOnly => _readOnly || _planMode;

    public bool PlanModeActive => _planMode;

    public void SetPlanMode(bool active) => _planMode = active;

    public IReadOnlyList<OllamaToolDefinition> Definitions
    {
        get
        {
            var definitions = new List<OllamaToolDefinition>
            {
                new("list_files", "List source files below the workspace root. Excludes secrets, build output, dependencies, and VCS internals.",
                    """{"type":"object","properties":{"path":{"type":"string","description":"Relative directory; default ."},"pattern":{"type":"string","description":"Simple glob such as *.cs; default *"},"max_results":{"type":"integer","minimum":1,"maximum":500}},"required":[]}"""),
                new("read_file", "Read a bounded line range from one non-secret text file in the workspace.",
                    """{"type":"object","properties":{"path":{"type":"string"},"start_line":{"type":"integer","minimum":1},"end_line":{"type":"integer","minimum":1}},"required":["path"]}"""),
                new("search_text", "Search plain text across non-secret workspace files and return file, line, and matching text.",
                    """{"type":"object","properties":{"query":{"type":"string"},"path":{"type":"string","description":"Relative directory; default ."},"glob":{"type":"string","description":"Simple file glob such as *.cs; default *"},"max_results":{"type":"integer","minimum":1,"maximum":200}},"required":["query"]}""")
            };
            if (!EffectiveReadOnly)
            {
                definitions.Add(new("replace_in_file", "Replace exact text in an existing workspace file after human confirmation. Prefer this for focused edits.",
                    """{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"},"replace_all":{"type":"boolean"}},"required":["path","old_text","new_text"]}"""));
                definitions.Add(new("write_file", "Create or replace one workspace text file after human confirmation. Prefer replace_in_file for focused edits.",
                    """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}"""));
                definitions.Add(new("run_command", "Run an allowlisted build, test, or read-only inspection command in the workspace after human confirmation. No shell command strings or destructive git operations.",
                    """{"type":"object","properties":{"program":{"type":"string","description":"Executable name such as dotnet, git, npm, rg, or bash"},"arguments":{"type":"array","items":{"type":"string"}},"working_directory":{"type":"string","description":"Relative directory; default ."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":900}},"required":["program"]}"""));
            }
            return definitions;
        }
    }

    public async Task<string> ExecuteAsync(OllamaToolCall call, CancellationToken cancellationToken)
    {
        if (!_quiet) Console.WriteLine($"  • {call.Name}");
        try
        {
            using var argumentsDocument = JsonDocument.Parse(call.ArgumentsJson);
            var arguments = argumentsDocument.RootElement;
            var result = call.Name switch
            {
                "list_files" => ListFiles(arguments),
                "read_file" => ReadFile(arguments),
                "search_text" => SearchText(arguments),
                "replace_in_file" when !EffectiveReadOnly => await ReplaceInFileAsync(arguments, cancellationToken),
                "write_file" when !EffectiveReadOnly => await WriteFileAsync(arguments, cancellationToken),
                "run_command" when !EffectiveReadOnly => await RunCommandAsync(arguments, cancellationToken),
                _ => JsonSerializer.Serialize(new { error = $"Unknown or disabled tool: {call.Name}" })
            };
            return Limit(result);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public string DescribeWorkspace()
    {
        var instructionFiles = new[] { "AGENTS.md", "CLAUDE.md", "README.md", "CONTRIBUTING.md" }
            .Where(file => File.Exists(Path.Combine(_root, file)))
            .ToArray();
        var repositories = DiscoverRepositories(16);
        return $"Workspace root: {_root}\n" +
               $"Repository roots: {(repositories.Count == 0 ? "none detected" : string.Join(", ", repositories))}\n" +
               $"Top-level instruction files: {(instructionFiles.Length == 0 ? "none detected" : string.Join(", ", instructionFiles))}\n" +
               $"Mode: {(_readOnly ? "read-only analysis" : "reviewable edits and allowlisted commands")}.";
    }

    private string ListFiles(JsonElement arguments)
    {
        var relativeDirectory = GetString(arguments, "path", ".");
        var pattern = GetString(arguments, "pattern", "*");
        var maxResults = Math.Clamp(GetInt(arguments, "max_results", 200), 1, 500);
        var directory = ResolvePath(relativeDirectory, requireExisting: true, expectDirectory: true);
        var files = EnumerateSafeFiles(directory)
            .Where(path => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path), ignoreCase: true)
                || FileSystemName.MatchesSimpleExpression(pattern, Path.GetRelativePath(_root, path), ignoreCase: true))
            .Select(path => Path.GetRelativePath(_root, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
        return JsonSerializer.Serialize(new { root = relativeDirectory, count = files.Length, files });
    }

    private string ReadFile(JsonElement arguments)
    {
        var relativePath = RequireString(arguments, "path");
        var path = ResolvePath(relativePath, requireExisting: true, expectDirectory: false);
        EnsureReadableFile(path);
        var startLine = Math.Max(1, GetInt(arguments, "start_line", 1));
        var endLine = Math.Max(startLine, GetInt(arguments, "end_line", startLine + MaxReadLines - 1));
        endLine = Math.Min(endLine, startLine + MaxReadLines - 1);
        var selected = File.ReadLines(path)
            .Skip(startLine - 1)
            .Take(endLine - startLine + 1)
            .Select((line, offset) => $"{startLine + offset,6}\t{line}")
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            path = Path.GetRelativePath(_root, path),
            startLine,
            endLine = selected.Length == 0 ? startLine : startLine + selected.Length - 1,
            content = string.Join('\n', selected)
        });
    }

    private string SearchText(JsonElement arguments)
    {
        var query = RequireString(arguments, "query");
        if (query.Length > 500)
            throw new ArgumentException("Search query is too long.");
        var relativeDirectory = GetString(arguments, "path", ".");
        var glob = GetString(arguments, "glob", "*");
        var maxResults = Math.Clamp(GetInt(arguments, "max_results", 80), 1, 200);
        var directory = ResolvePath(relativeDirectory, requireExisting: true, expectDirectory: true);
        var matches = new List<object>();
        foreach (var file in EnumerateSafeFiles(directory))
        {
            if (!FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(file), ignoreCase: true)
                && !FileSystemName.MatchesSimpleExpression(glob, Path.GetRelativePath(_root, file), ignoreCase: true))
                continue;
            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    var index = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;
                    matches.Add(new
                    {
                        path = Path.GetRelativePath(_root, file),
                        line = lineNumber,
                        text = line.Length <= 500 ? line : line[..500]
                    });
                    if (matches.Count >= maxResults)
                        return JsonSerializer.Serialize(new { query, matches, truncated = true });
                }
            }
            catch (DecoderFallbackException)
            {
                // Binary or incompatible text encoding; skip it.
            }
        }
        return JsonSerializer.Serialize(new { query, matches, truncated = false });
    }

    private async Task<string> ReplaceInFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var relativePath = RequireString(arguments, "path");
        var oldText = RequireString(arguments, "old_text", allowEmpty: false);
        var newText = GetString(arguments, "new_text", string.Empty);
        var replaceAll = GetBool(arguments, "replace_all");
        var path = ResolvePath(relativePath, requireExisting: true, expectDirectory: false);
        EnsureReadableFile(path);
        var content = File.ReadAllText(path);
        var occurrences = CountOccurrences(content, oldText);
        if (occurrences == 0)
            return JsonSerializer.Serialize(new { error = "The exact old_text was not found; re-read the file before editing." });
        if (!replaceAll && occurrences != 1)
            return JsonSerializer.Serialize(new { error = $"old_text matched {occurrences} times; provide more context or set replace_all=true." });

        var detail = $"{Path.GetRelativePath(_root, path)}\n" +
                     $"Replace {(replaceAll ? occurrences : 1)} occurrence(s)\n" +
                     $"OLD:\n{Preview(oldText)}\nNEW:\n{Preview(newText)}";
        if (!await _approval.ConfirmAsync("file replacement", detail, cancellationToken))
            return JsonSerializer.Serialize(new { approved = false, changed = false });

        var updated = replaceAll
            ? content.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(content, oldText, newText);
        await WriteAtomicallyAsync(path, updated, cancellationToken);
        return JsonSerializer.Serialize(new { approved = true, changed = true, path = Path.GetRelativePath(_root, path) });
    }

    private async Task<string> WriteFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var relativePath = RequireString(arguments, "path");
        var content = GetString(arguments, "content", string.Empty);
        if (content.Length > MaxWriteCharacters)
            throw new ArgumentException($"File content exceeds {MaxWriteCharacters:N0} characters.");
        var path = ResolvePath(relativePath, requireExisting: false, expectDirectory: false);
        if (File.Exists(path))
            EnsureReadableFile(path);
        var existed = File.Exists(path);
        var detail = $"{Path.GetRelativePath(_root, path)} ({(existed ? "replace" : "create")}, {content.Length:N0} characters)\n" +
                     Preview(content);
        if (!await _approval.ConfirmAsync("file write", detail, cancellationToken))
            return JsonSerializer.Serialize(new { approved = false, changed = false });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteAtomicallyAsync(path, content, cancellationToken);
        return JsonSerializer.Serialize(new { approved = true, changed = true, created = !existed, path = Path.GetRelativePath(_root, path) });
    }

    private async Task<string> RunCommandAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var program = RequireString(arguments, "program");
        if (Path.GetFileName(program) != program || !AllowedPrograms.Contains(program))
            throw new ArgumentException($"Program is not allowlisted: {program}");
        var commandArguments = GetStringArray(arguments, "arguments");
        var relativeDirectory = GetString(arguments, "working_directory", ".");
        var workingDirectory = ResolvePath(relativeDirectory, requireExisting: true, expectDirectory: true);
        ValidateCommand(program, commandArguments, workingDirectory);
        var timeoutSeconds = Math.Clamp(GetInt(arguments, "timeout_seconds", 300), 1, 900);
        var detail = $"Directory: {Path.GetRelativePath(_root, workingDirectory)}\nCommand: " +
                     string.Join(' ', new[] { program }.Concat(commandArguments).Select(ShellDisplay));
        if (!await _approval.ConfirmAsync("command", detail, cancellationToken))
            return JsonSerializer.Serialize(new { approved = false, executed = false });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var startInfo = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in commandArguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {program}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return JsonSerializer.Serialize(new { executed = true, timedOut = true, timeoutSeconds });
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return JsonSerializer.Serialize(new
        {
            executed = true,
            exitCode = process.ExitCode,
            stdout = Limit(stdout),
            stderr = Limit(stderr)
        });
    }

    private IEnumerable<string> EnumerateSafeFiles(string startDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(startDirectory);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> subdirectories;
            IEnumerable<string> files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory).ToArray();
                files = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var subdirectory in subdirectories)
            {
                if (ExcludedDirectories.Contains(Path.GetFileName(subdirectory)) || IsLink(subdirectory))
                    continue;
                pending.Push(subdirectory);
            }
            foreach (var file in files)
            {
                if (IsSecretPath(file) || IsLink(file))
                    continue;
                var info = new FileInfo(file);
                if (info.Length <= MaxFileBytes)
                    yield return file;
            }
        }
    }

    private IReadOnlyList<string> DiscoverRepositories(int maxResults)
    {
        var results = new List<string>();
        if (Directory.Exists(Path.Combine(_root, ".git")))
            results.Add(".");
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            if (results.Count >= maxResults) break;
            if (ExcludedDirectories.Contains(Path.GetFileName(directory))) continue;
            if (Directory.Exists(Path.Combine(directory, ".git")))
                results.Add(Path.GetRelativePath(_root, directory));
        }
        return results;
    }

    private string ResolvePath(string relativePath, bool requireExisting, bool expectDirectory)
    {
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("Paths must be relative to the workspace root.");
        var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!string.Equals(fullPath, _root, StringComparison.Ordinal) && !fullPath.StartsWith(_rootPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Path escapes the workspace root.");
        if (IsSecretPath(fullPath))
            throw new ArgumentException("Access to secret or credential files is blocked.");

        var existingPath = File.Exists(fullPath) || Directory.Exists(fullPath);
        if (requireExisting && !existingPath)
            throw new ArgumentException($"Path does not exist: {relativePath}");
        if (existingPath && expectDirectory != Directory.Exists(fullPath))
            throw new ArgumentException(expectDirectory ? "Expected a directory." : "Expected a file.");
        EnsureNoEscapingLink(fullPath);
        return fullPath;
    }

    private void EnsureNoEscapingLink(string fullPath)
    {
        var current = _root;
        var relative = Path.GetRelativePath(_root, fullPath);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is null) continue;
            var resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new ArgumentException("Could not resolve symbolic link.");
            if (!string.Equals(resolved, _root, StringComparison.Ordinal) && !resolved.StartsWith(_rootPrefix, StringComparison.Ordinal))
                throw new ArgumentException("Symbolic link escapes the workspace root.");
        }
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsSecretPath(string path)
    {
        var name = Path.GetFileName(path);
        if (SecretFileNames.Contains(name) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".local.json", StringComparison.OrdinalIgnoreCase))
            return true;
        var extension = Path.GetExtension(name);
        if (extension is ".pem" or ".pfx" or ".p12" or ".key")
            return true;
        return path.Split(Path.DirectorySeparatorChar)
            .Any(segment => segment.Equals(".ssh", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".aws", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".azure", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".kube", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".gnupg", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureReadableFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
            throw new ArgumentException($"File exceeds the {MaxFileBytes:N0}-byte read limit.");
        using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(4096, (int)info.Length)];
        var count = stream.Read(buffer);
        if (buffer.AsSpan(0, count).Contains((byte)0))
            throw new ArgumentException("Binary files cannot be read or edited.");
    }

    // Also used by SessionStore for the same reason: a temp-file-then-atomic-move avoids a torn
    // partial file if the process is killed mid-write.
    internal static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void ValidateCommand(string program, IReadOnlyList<string> arguments, string workingDirectory)
    {
        foreach (var argument in arguments)
        {
            if (argument.Contains('\0'))
                throw new ArgumentException("Command arguments contain invalid characters.");
            if (argument.Split('/', '\\').Any(segment => segment == ".."))
                throw new ArgumentException("Command arguments cannot traverse outside the workspace.");
            if (Path.IsPathRooted(argument))
            {
                var fullPath = Path.GetFullPath(argument);
                if (!string.Equals(fullPath, _root, StringComparison.Ordinal)
                    && !fullPath.StartsWith(_rootPrefix, StringComparison.Ordinal))
                    throw new ArgumentException("Command path escapes the workspace root.");
            }
            if (LooksLikePath(argument))
            {
                var candidate = Path.GetFullPath(Path.Combine(workingDirectory, argument));
                if (!string.Equals(candidate, _root, StringComparison.Ordinal)
                    && !candidate.StartsWith(_rootPrefix, StringComparison.Ordinal))
                    throw new ArgumentException("Command path escapes the workspace root.");
                if (IsSecretPath(candidate))
                    throw new ArgumentException("Command access to secret or credential files is blocked.");
            }
        }

        if (program.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Any(argument => argument is "-C" or "--git-dir" or "--work-tree"
                || argument.StartsWith("--git-dir=", StringComparison.Ordinal)
                || argument.StartsWith("--work-tree=", StringComparison.Ordinal)))
                throw new ArgumentException("Git repository redirection is not allowed.");
            var subcommand = arguments.FirstOrDefault(argument => !argument.StartsWith('-'));
            if (subcommand is null || !ReadOnlyGitCommands.Contains(subcommand))
                throw new ArgumentException("Only read-only git commands are allowed; file edits use reviewable file tools.");
        }
        if (program.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var subcommand = arguments.FirstOrDefault(argument => !argument.StartsWith('-'));
            string[] allowed = ["restore", "build", "test", "format", "publish", "pack", "clean", "msbuild", "ef"];
            if (subcommand is null || !allowed.Contains(subcommand, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("dotnet is limited to restore, build, test, format, publish, pack, clean, msbuild, and ef validation workflows.");
        }
        if (program.Equals("npm", StringComparison.OrdinalIgnoreCase))
        {
            var subcommand = arguments.FirstOrDefault(argument => !argument.StartsWith('-'));
            if (subcommand is not ("ci" or "test" or "run"))
                throw new ArgumentException("npm is limited to ci, test, and reviewed build/check scripts.");
            if (subcommand == "run")
            {
                var script = arguments.SkipWhile(argument => argument != "run").Skip(1).FirstOrDefault();
                string[] safePrefixes = ["test", "build", "lint", "check", "typecheck", "verify", "format"];
                if (script is null || !safePrefixes.Any(prefix => script.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("npm run is limited to test, build, lint, check, typecheck, verify, and format scripts.");
            }
        }
        if (program.Equals("node", StringComparison.OrdinalIgnoreCase)
            && (arguments.Count != 2 || arguments[0] != "--check"))
            throw new ArgumentException("node is limited to syntax checks such as 'node --check file.js'.");
        if (program is "python" or "python3")
        {
            string[] safeModules = ["pytest", "unittest", "compileall"];
            if (arguments.Count < 2 || arguments[0] != "-m" || !safeModules.Contains(arguments[1], StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Python is limited to pytest, unittest, and compileall module validation.");
        }
        if (program is "bash" or "sh" && (arguments.Count != 2 || arguments[0] != "-n"))
            throw new ArgumentException("Shells are limited to syntax checks such as 'bash -n script.sh'.");
        if (program.Equals("find", StringComparison.OrdinalIgnoreCase)
            && arguments.Any(argument => argument is "-exec" or "-execdir" or "-delete" or "-ok" or "-okdir"))
            throw new ArgumentException("find execution and deletion actions are not allowed.");
        if (program.Equals("sed", StringComparison.OrdinalIgnoreCase)
            && arguments.Any(argument => argument == "-i" || argument.StartsWith("-i", StringComparison.Ordinal)))
            throw new ArgumentException("sed in-place edits are not allowed; use the reviewable file tools.");
        if (program.Equals("rg", StringComparison.OrdinalIgnoreCase)
            && arguments.Any(argument => argument == "--pre" || argument.StartsWith("--pre=", StringComparison.Ordinal)))
            throw new ArgumentException("rg preprocessor execution is not allowed.");
    }

    private static bool LooksLikePath(string argument) =>
        !argument.StartsWith("-", StringComparison.Ordinal)
        && (argument.StartsWith(".", StringComparison.Ordinal)
            || argument.Contains('/')
            || argument.Contains('\\'));

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string content, string oldText, string newText)
    {
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        return content[..index] + newText + content[(index + oldText.Length)..];
    }

    private static string RequireString(JsonElement arguments, string property, bool allowEmpty = false)
    {
        if (!arguments.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{property} is required.");
        var value = element.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{property} cannot be empty.");
        return value;
    }

    private static string GetString(JsonElement arguments, string property, string fallback) =>
        arguments.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : fallback;

    private static int GetInt(JsonElement arguments, string property, int fallback)
    {
        if (!arguments.TryGetProperty(property, out var element)) return fallback;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numericValue)) return numericValue;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var stringValue)) return stringValue;
        return fallback;
    }

    private static bool GetBool(JsonElement arguments, string property) =>
        arguments.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.True;

    private static string[] GetStringArray(JsonElement arguments, string property) =>
        arguments.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];

    private static string Preview(string value)
    {
        const int limit = 3_000;
        return value.Length <= limit ? value : value[..limit] + "\n... preview truncated ...";
    }

    private static string Limit(string value) =>
        value.Length <= MaxToolOutput ? value : value[..MaxToolOutput] + "\n... tool output truncated ...";

    private static string ShellDisplay(string value) =>
        value.All(character => char.IsAsciiLetterOrDigit(character) || "-_=./:".Contains(character))
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
