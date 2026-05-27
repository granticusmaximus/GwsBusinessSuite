namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class ReactPageReference
{
    public string PageKey { get; set; } = string.Empty;
    public string RoutePath { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class VisualBuilderElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ElementType { get; set; } = "heading";
    public string Text { get; set; } = string.Empty;
    public string CssClass { get; set; } = string.Empty;
}

public sealed class ReactPageEditorState
{
    public string PageKey { get; set; } = string.Empty;
    public string RoutePath { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<VisualBuilderElement> Elements { get; set; } = new();
}

public sealed class ReactPageSaveRequest
{
    public string PageKey { get; set; } = string.Empty;
    public List<VisualBuilderElement> Elements { get; set; } = new();
    public string PerformedBy { get; set; } = "cms-builder-ui";
}

public sealed class ReactPageSaveResult
{
    public bool SavedToFile { get; set; }
    public string SavedFilePath { get; set; } = string.Empty;
    public bool GitAutoPushAttempted { get; set; }
    public bool GitAutoPushSucceeded { get; set; }
    public string GitSummary { get; set; } = string.Empty;
}

public sealed class ReactPublishStatus
{
    public string CurrentBranch { get; set; } = string.Empty;
    public List<string> ChangedFiles { get; set; } = new();
    public bool HasChanges => ChangedFiles.Count > 0;
}

public sealed class ReactPublishRequest
{
    public string CommitMessage { get; set; } = string.Empty;
    public bool IncludeOnlyReactAppChanges { get; set; } = true;
}

public sealed class ReactPublishResult
{
    public bool CommitCreated { get; set; }
    public bool PushAttempted { get; set; }
    public bool PushSucceeded { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> PublishedFiles { get; set; } = new();
}

public sealed class CmsBuilderOptions
{
    public const string SectionName = "CmsBuilder";

    public string ReactAppRelativePath { get; set; } = "apps/public-site";
    public bool AutoGitPushOnSave { get; set; }
    public string GitCommitPrefix { get; set; } = "cms-builder";
    public string GitPublishRemoteName { get; set; } = "origin";
    public string GitPublishBranch { get; set; } = "main";
    public bool RequireGithubTokenForPush { get; set; } = true;
    public string GithubTokenEnvironmentVariable { get; set; } = "GWS_GITHUB_TOKEN";
}
