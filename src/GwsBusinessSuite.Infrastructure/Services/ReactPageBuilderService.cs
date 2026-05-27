using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class ReactPageBuilderService(
    IHostEnvironment hostEnvironment,
    IOptions<CmsBuilderOptions> options) : IReactPageBuilderService
{
    private static readonly Regex RouteRegex = new("<Route\\s+path=\"(?<path>[^\"]+)\"\\s+element={<(?<component>[A-Za-z0-9_]+)", RegexOptions.Compiled);

    public async Task<IReadOnlyList<ReactPageReference>> ListReactPagesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pagesDirectory = GetReactPagesDirectory();
        if (!Directory.Exists(pagesDirectory))
        {
            return [];
        }

        var routesByComponent = await LoadRoutesByComponentAsync(cancellationToken);

        var pageFiles = Directory
            .GetFiles(pagesDirectory, "*.jsx", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFileNameWithoutExtension(path), "AdminRedirect", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var references = new List<ReactPageReference>(pageFiles.Count);
        foreach (var file in pageFiles)
        {
            var pageKey = Path.GetFileNameWithoutExtension(file);
            routesByComponent.TryGetValue(pageKey, out var routePath);
            references.Add(new ReactPageReference
            {
                PageKey = pageKey,
                DisplayName = SplitPascalCase(pageKey),
                RoutePath = routePath ?? InferRoutePath(pageKey),
                FilePath = file
            });
        }

        return references;
    }

    public async Task<ReactPageEditorState?> LoadEditorStateAsync(string pageKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageKey))
        {
            return null;
        }

        var pages = await ListReactPagesAsync(cancellationToken);
        var target = pages.FirstOrDefault(x => string.Equals(x.PageKey, pageKey, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return null;
        }

        var source = await File.ReadAllTextAsync(target.FilePath, cancellationToken);
        var elements = ParseElementsFromManagedSource(source);
        if (elements.Count == 0)
        {
            elements =
            [
                new VisualBuilderElement { ElementType = "heading", Text = target.DisplayName, CssClass = "" },
                new VisualBuilderElement { ElementType = "paragraph", Text = "Add your content here.", CssClass = "" }
            ];
        }

        return new ReactPageEditorState
        {
            PageKey = target.PageKey,
            RoutePath = target.RoutePath,
            FilePath = target.FilePath,
            Elements = elements
        };
    }

    public async Task<ReactPageSaveResult> SaveAsync(ReactPageSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var editorState = await LoadEditorStateAsync(request.PageKey, cancellationToken);
        if (editorState is null)
        {
            throw new InvalidOperationException($"React page '{request.PageKey}' could not be found.");
        }

        var generatedSource = BuildManagedPageSource(editorState.PageKey, request.Elements);
        await File.WriteAllTextAsync(editorState.FilePath, generatedSource, cancellationToken);

        var result = new ReactPageSaveResult
        {
            SavedToFile = true,
            SavedFilePath = editorState.FilePath,
            GitSummary = "Saved to React page file."
        };

        if (!options.Value.AutoGitPushOnSave)
        {
            return result;
        }

        var repoRoot = GetRepositoryRoot();
        var relativeFilePath = Path.GetRelativePath(repoRoot, editorState.FilePath).Replace('\\', '/');
        var commitMessage = $"{options.Value.GitCommitPrefix.Trim()} update {request.PageKey} from visual builder";

        result.GitAutoPushAttempted = true;

        var addExit = await RunGitCommandAsync(repoRoot, $"add \"{relativeFilePath}\"", cancellationToken);
        if (addExit.ExitCode != 0)
        {
            result.GitSummary = $"Saved file, but git add failed: {addExit.Output}";
            return result;
        }

        var commitExit = await RunGitCommandAsync(repoRoot, $"commit -m \"{EscapeCommitMessage(commitMessage)}\"", cancellationToken);
        if (commitExit.ExitCode != 0)
        {
            result.GitSummary = $"Saved file, but git commit failed or no changes to commit: {commitExit.Output}";
            return result;
        }

        var pushExit = await RunGitCommandAsync(repoRoot, "push", cancellationToken);
        result.GitAutoPushSucceeded = pushExit.ExitCode == 0;
        result.GitSummary = pushExit.ExitCode == 0
            ? $"Saved file and pushed commit successfully. {pushExit.Output}"
            : $"Saved file and committed, but git push failed: {pushExit.Output}";

        return result;
    }

    public async Task<ReactPublishStatus> GetPublishStatusAsync(CancellationToken cancellationToken = default)
    {
        var repoRoot = GetRepositoryRoot();
        var branchExit = await RunGitCommandAsync(repoRoot, "rev-parse --abbrev-ref HEAD", cancellationToken);
        var statusExit = await RunGitCommandAsync(repoRoot, "status --porcelain", cancellationToken);

        var changedFiles = ParseChangedFiles(statusExit.Output)
            .Where(IsInsideReactApp)
            .ToList();

        return new ReactPublishStatus
        {
            CurrentBranch = branchExit.ExitCode == 0 ? branchExit.Output.Trim() : "unknown",
            ChangedFiles = changedFiles
        };
    }

    public async Task<ReactPublishResult> PublishAsync(ReactPublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repoRoot = GetRepositoryRoot();
        var status = await GetPublishStatusAsync(cancellationToken);
        var filesToPublish = request.IncludeOnlyReactAppChanges
            ? status.ChangedFiles
            : ParseChangedFiles((await RunGitCommandAsync(repoRoot, "status --porcelain", cancellationToken)).Output).ToList();

        if (filesToPublish.Count == 0)
        {
            return new ReactPublishResult
            {
                Summary = "No eligible file changes were found to publish.",
                PublishedFiles = new List<string>()
            };
        }

        foreach (var file in filesToPublish)
        {
            var addExit = await RunGitCommandAsync(repoRoot, $"add \"{file}\"", cancellationToken);
            if (addExit.ExitCode != 0)
            {
                return new ReactPublishResult
                {
                    Summary = $"git add failed for {file}: {addExit.Output}",
                    PublishedFiles = filesToPublish
                };
            }
        }

        var commitMessage = string.IsNullOrWhiteSpace(request.CommitMessage)
            ? $"{options.Value.GitCommitPrefix.Trim()} publish cms builder updates"
            : request.CommitMessage.Trim();

        var commitExit = await RunGitCommandAsync(repoRoot, $"commit -m \"{EscapeCommitMessage(commitMessage)}\"", cancellationToken);
        if (commitExit.ExitCode != 0)
        {
            return new ReactPublishResult
            {
                Summary = $"git commit failed: {commitExit.Output}",
                PublishedFiles = filesToPublish
            };
        }

        var shaExit = await RunGitCommandAsync(repoRoot, "rev-parse HEAD", cancellationToken);
        var result = new ReactPublishResult
        {
            CommitCreated = true,
            CommitSha = shaExit.ExitCode == 0 ? shaExit.Output.Trim() : string.Empty,
            PublishedFiles = filesToPublish
        };

        var remoteName = options.Value.GitPublishRemoteName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(remoteName))
        {
            result.Summary = "Commit created. Push skipped because no publish remote is configured.";
            return result;
        }

        result.PushAttempted = true;
        var pushBranch = string.IsNullOrWhiteSpace(options.Value.GitPublishBranch)
            ? "main"
            : options.Value.GitPublishBranch.Trim();

        var pushArgs = $"push {remoteName} HEAD:{pushBranch}";
        Dictionary<string, string>? envOverrides = null;

        if (options.Value.RequireGithubTokenForPush)
        {
            var tokenEnvVarName = string.IsNullOrWhiteSpace(options.Value.GithubTokenEnvironmentVariable)
                ? "GWS_GITHUB_TOKEN"
                : options.Value.GithubTokenEnvironmentVariable;
            var token = Environment.GetEnvironmentVariable(tokenEnvVarName);
            if (string.IsNullOrWhiteSpace(token))
            {
                result.Summary = $"Commit created, but push blocked: environment variable '{tokenEnvVarName}' is not set.";
                return result;
            }

            pushArgs = $"-c http.extraheader=\"AUTHORIZATION: bearer {token}\" push {remoteName} HEAD:{pushBranch}";
            envOverrides = new Dictionary<string, string>
            {
                [tokenEnvVarName] = token
            };
        }

        var pushExit = await RunGitCommandAsync(repoRoot, pushArgs, cancellationToken, envOverrides);
        result.PushSucceeded = pushExit.ExitCode == 0;
        result.Summary = pushExit.ExitCode == 0
            ? $"Published successfully. Commit {result.CommitSha} pushed to {remoteName}/{pushBranch}."
            : $"Commit created ({result.CommitSha}), but push failed: {pushExit.Output}";

        return result;
    }

    private string GetRepositoryRoot()
    {
        var webProjectRoot = hostEnvironment.ContentRootPath;
        return Path.GetFullPath(Path.Combine(webProjectRoot, "..", ".."));
    }

    private string GetReactAppRoot()
    {
        var repoRoot = GetRepositoryRoot();
        var relative = string.IsNullOrWhiteSpace(options.Value.ReactAppRelativePath)
            ? "apps/public-site"
            : options.Value.ReactAppRelativePath.Trim();

        return Path.GetFullPath(Path.Combine(repoRoot, relative));
    }

    private string GetReactPagesDirectory()
    {
        return Path.Combine(GetReactAppRoot(), "src", "pages");
    }

    private string GetReactAppFilePath()
    {
        return Path.Combine(GetReactAppRoot(), "src", "App.jsx");
    }

    private async Task<Dictionary<string, string>> LoadRoutesByComponentAsync(CancellationToken cancellationToken)
    {
        var appFilePath = GetReactAppFilePath();
        if (!File.Exists(appFilePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var appFile = await File.ReadAllTextAsync(appFilePath, cancellationToken);
        var routeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in RouteRegex.Matches(appFile))
        {
            var component = match.Groups["component"].Value;
            var path = match.Groups["path"].Value;
            if (!string.IsNullOrWhiteSpace(component) && !string.IsNullOrWhiteSpace(path))
            {
                routeMap[component] = path;
            }
        }

        return routeMap;
    }

    private static List<VisualBuilderElement> ParseElementsFromManagedSource(string source)
    {
        const string beginMarker = "/* GWS_VISUAL_BUILDER_JSON_START */";
        const string endMarker = "/* GWS_VISUAL_BUILDER_JSON_END */";

        var startIndex = source.IndexOf(beginMarker, StringComparison.Ordinal);
        var endIndex = source.IndexOf(endMarker, StringComparison.Ordinal);
        if (startIndex < 0 || endIndex <= startIndex)
        {
            return [];
        }

        var jsonStart = startIndex + beginMarker.Length;
        var json = source[jsonStart..endIndex].Trim();

        try
        {
            var parsed = JsonSerializer.Deserialize<List<VisualBuilderElement>>(json);
            return parsed ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildManagedPageSource(string pageKey, IReadOnlyList<VisualBuilderElement> elements)
    {
        var safeElements = elements
            .Where(element => !string.IsNullOrWhiteSpace(element.ElementType))
            .Select(element => new VisualBuilderElement
            {
                Id = string.IsNullOrWhiteSpace(element.Id) ? Guid.NewGuid().ToString("N") : element.Id.Trim(),
                ElementType = element.ElementType.Trim().ToLowerInvariant(),
                Text = element.Text ?? string.Empty,
                CssClass = element.CssClass ?? string.Empty
            })
            .ToList();

        var json = JsonSerializer.Serialize(safeElements, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var componentName = string.IsNullOrWhiteSpace(pageKey) ? "ManagedPage" : pageKey.Trim();
        var jsx = new StringBuilder();
        jsx.AppendLine("import React from 'react';");
        jsx.AppendLine();
        jsx.AppendLine($"const {componentName} = () => {{");
        jsx.AppendLine("  const renderElement = (element) => {");
        jsx.AppendLine("    const key = element.id;");
        jsx.AppendLine("    const className = element.cssClass || ''; ");
        jsx.AppendLine("    switch (element.elementType) {");
        jsx.AppendLine("      case 'heading':");
        jsx.AppendLine("        return <h2 key={key} className={className}>{element.text}</h2>;");
        jsx.AppendLine("      case 'button':");
        jsx.AppendLine("        return <button key={key} className={className} type=\"button\">{element.text}</button>;");
        jsx.AppendLine("      case 'image':");
        jsx.AppendLine("        return <img key={key} className={className} src={element.text} alt=\"\" />;");
        jsx.AppendLine("      case 'paragraph':");
        jsx.AppendLine("      default:");
        jsx.AppendLine("        return <p key={key} className={className}>{element.text}</p>;");
        jsx.AppendLine("    }");
        jsx.AppendLine("  };");
        jsx.AppendLine();
        jsx.AppendLine("  const elements = [");
        jsx.AppendLine("/* GWS_VISUAL_BUILDER_JSON_START */");
        foreach (var line in json.Split('\n'))
        {
            jsx.AppendLine($"{line}");
        }
        jsx.AppendLine("/* GWS_VISUAL_BUILDER_JSON_END */");
        jsx.AppendLine("  ];");
        jsx.AppendLine();
        jsx.AppendLine("  return (");
        jsx.AppendLine("    <div className=\"gws-managed-page\" style={{ padding: '2rem' }}>");
        jsx.AppendLine("      {elements.map(renderElement)}");
        jsx.AppendLine("    </div>");
        jsx.AppendLine("  );");
        jsx.AppendLine("};");
        jsx.AppendLine();
        jsx.AppendLine($"export default {componentName};");

        return jsx.ToString();
    }

    private static string InferRoutePath(string pageKey)
    {
        if (string.Equals(pageKey, "Home", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return "/" + pageKey.ToLowerInvariant();
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string EscapeCommitMessage(string message)
    {
        return message.Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private bool IsInsideReactApp(string relativePath)
    {
        var normalizedRelative = relativePath.Replace('\\', '/').Trim();
        var reactPrefix = (options.Value.ReactAppRelativePath ?? "apps/public-site").Replace('\\', '/').Trim('/');
        return normalizedRelative.StartsWith(reactPrefix + "/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedRelative, reactPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ParseChangedFiles(string porcelainStatus)
    {
        if (string.IsNullOrWhiteSpace(porcelainStatus))
        {
            return Array.Empty<string>();
        }

        var files = new List<string>();
        foreach (var rawLine in porcelainStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 3)
            {
                continue;
            }

            var file = line.Length > 3 ? line[3..].Trim() : string.Empty;
            if (!string.IsNullOrWhiteSpace(file))
            {
                files.Add(file.Replace('\\', '/'));
            }
        }

        return files;
    }

    private static async Task<(int ExitCode, string Output)> RunGitCommandAsync(
        string workingDirectory,
        string arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environmentOverrides is not null)
        {
            foreach (var pair in environmentOverrides)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = (await stdOutTask) + (await stdErrTask);
        return (process.ExitCode, output.Trim());
    }
}
