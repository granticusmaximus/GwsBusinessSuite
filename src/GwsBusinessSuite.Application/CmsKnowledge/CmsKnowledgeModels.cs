namespace GwsBusinessSuite.Application.CmsKnowledge;

public sealed class CmsKnowledgeSourceEditorModel
{
    public Guid? SourceId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LicenseNotes { get; set; } = string.Empty;
    public string UsageGuidance { get; set; } = string.Empty;
}

public sealed class CmsKnowledgeEntryEditorModel
{
    public Guid? EntryId { get; set; }
    public Guid SourceId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public string WorkflowSummary { get; set; } = string.Empty;
    public string ImplementationHint { get; set; } = string.Empty;
    // Comma-separated, same convention as the entity's SuggestedBlocksCsv.
    public string SuggestedBlocks { get; set; } = string.Empty;
}

public sealed class CmsKnowledgeQueryResult
{
    public Guid SourceId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string WorkflowSummary { get; set; } = string.Empty;
    public string ImplementationHint { get; set; } = string.Empty;
    public string[] SuggestedBlocks { get; set; } = [];
    public int Score { get; set; }
}
