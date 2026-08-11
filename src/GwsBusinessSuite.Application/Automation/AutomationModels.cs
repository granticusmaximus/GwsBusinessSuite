using System.Text.Json;

namespace GwsBusinessSuite.Application.Automation;

public sealed record AutomationWorkflowSummary(
    Guid Id,
    string Name,
    string Description,
    string Status,
    int CurrentVersion,
    int NodeCount,
    DateTimeOffset? LastExecutedAt,
    DateTimeOffset UpdatedAt);

public sealed class AutomationWorkflowView
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string TagsCsv { get; init; } = string.Empty;
    public int CurrentVersion { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public IReadOnlyList<AutomationNodeView> Nodes { get; init; } = [];
    public IReadOnlyList<AutomationConnectionView> Connections { get; init; } = [];
    public IReadOnlyList<AutomationExecutionSummary> RecentExecutions { get; init; } = [];
}

public sealed record AutomationNodeView(
    Guid Id,
    string Name,
    string TypeKey,
    int TypeVersion,
    double PositionX,
    double PositionY,
    string ParametersJson,
    Guid? CredentialId,
    bool IsDisabled,
    bool ContinueOnFail,
    bool RetryOnFail,
    int MaxTries,
    int WaitBetweenTriesMs,
    int TimeoutMs,
    string Notes);

public sealed record AutomationConnectionView(
    Guid Id,
    Guid SourceNodeId,
    string SourceOutput,
    Guid TargetNodeId,
    string TargetInput);

public sealed record AutomationExecutionSummary(
    Guid Id,
    string Mode,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string ErrorMessage);

public sealed record AutomationWaitStatus(
    string WaitingNodeTypeKey,
    string WaitingNodeName,
    DateTimeOffset? ResumeAt);

public sealed record AutomationCredentialSummary(
    Guid Id,
    string Name,
    string TypeKey,
    string Description,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset UpdatedAt);

// Denormalizes the workflow name so the cross-workflow failures panel doesn't need a second
// round trip per row - an admin scanning this list wants to know which workflow broke, not
// just an execution GUID they'd have to look up separately.
public sealed record AutomationRecentFailureView(
    Guid ExecutionId,
    Guid WorkflowId,
    string WorkflowName,
    string Mode,
    string ErrorMessage,
    DateTimeOffset? FinishedAt);

public sealed record AutomationExecutionView(
    Guid Id,
    Guid WorkflowId,
    int WorkflowVersion,
    string Mode,
    string Status,
    string InputJson,
    string OutputJson,
    string ErrorMessage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    AutomationWaitStatus? Wait,
    IReadOnlyList<AutomationNodeExecutionView> Nodes);

public sealed record AutomationNodeExecutionView(
    Guid Id,
    Guid NodeId,
    string NodeName,
    string NodeTypeKey,
    string Status,
    int Attempt,
    string InputJson,
    string OutputJson,
    string ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record AutomationNodeDefinition(
    string TypeKey,
    int Version,
    string DisplayName,
    string Description,
    string Category,
    string IconClass,
    bool IsTrigger,
    IReadOnlyList<string> Outputs,
    string DefaultParametersJson,
    // Whether re-running this node type with the same input is safe. Defaults true (every
    // pure data/flow node genuinely is); core.httpRequest and database.setRowProperty are the
    // only two with a real external side effect and are tagged false at their definitions.
    // Consulted by AutomationExecutionService.ResumeAsync's orphan-recovery path, which can
    // replay a work item whose node already ran once before a crash - see the checkpoint-
    // cadence comment on AutomationExecutionService.RunLoopAsync for why that window exists.
    bool IsIdempotent = true);

public sealed class AutomationNodeEditor
{
    public Guid? Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string TypeKey { get; init; } = string.Empty;
    public int TypeVersion { get; init; } = 1;
    public double PositionX { get; init; }
    public double PositionY { get; init; }
    public string ParametersJson { get; init; } = "{}";
    public Guid? CredentialId { get; init; }
    public bool IsDisabled { get; init; }
    public bool ContinueOnFail { get; init; }
    public bool RetryOnFail { get; init; }
    public int MaxTries { get; init; } = 1;
    public int WaitBetweenTriesMs { get; init; }
    public int TimeoutMs { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public sealed record AutomationValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed class AutomationWorkflowSnapshot
{
    public Guid WorkflowId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; }
    public IReadOnlyList<AutomationNodeSnapshot> Nodes { get; init; } = [];
    public IReadOnlyList<AutomationConnectionSnapshot> Connections { get; init; } = [];
}

public sealed record AutomationNodeSnapshot(
    Guid Id,
    string Name,
    string TypeKey,
    int TypeVersion,
    string ParametersJson,
    Guid? CredentialId,
    bool IsDisabled,
    bool ContinueOnFail,
    bool RetryOnFail,
    int MaxTries,
    int WaitBetweenTriesMs,
    int TimeoutMs);

public sealed record AutomationConnectionSnapshot(
    Guid SourceNodeId,
    string SourceOutput,
    Guid TargetNodeId,
    string TargetInput);

public sealed record AutomationNodeRunResult(
    IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> Outputs,
    string DisplayOutputJson);

public sealed record AutomationHttpRequest(
    HttpMethod Method,
    string Url,
    string? Body,
    IReadOnlyDictionary<string, string> Headers);

public sealed record AutomationHttpResponse(int StatusCode, string Body, IReadOnlyDictionary<string, string> Headers);
