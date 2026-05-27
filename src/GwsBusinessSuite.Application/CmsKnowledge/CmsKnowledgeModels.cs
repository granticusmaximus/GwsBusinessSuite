namespace GwsBusinessSuite.Application.CmsKnowledge;

public sealed class CmsKnowledgeSource
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LicenseNotes { get; set; } = string.Empty;
    public string UsageGuidance { get; set; } = string.Empty;
}

public sealed class CmsKnowledgeEntry
{
    public string SourceKey { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string WorkflowSummary { get; set; } = string.Empty;
    public string ImplementationHint { get; set; } = string.Empty;
    public string[] SuggestedBlocks { get; set; } = [];
}

public sealed class CmsKnowledgeQueryResult
{
    public string SourceKey { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string WorkflowSummary { get; set; } = string.Empty;
    public string ImplementationHint { get; set; } = string.Empty;
    public string[] SuggestedBlocks { get; set; } = [];
    public int Score { get; set; }
}
