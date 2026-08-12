using System.Text.Json;

namespace GwsBusinessSuite.Application.Automation;

public interface IAutomationWorkflowService
{
    Task<IReadOnlyList<AutomationWorkflowSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<AutomationWorkflowView?> GetAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<AutomationWorkflowView> CreateAsync(string name, string description = "", CancellationToken cancellationToken = default);
    Task UpdateMetadataAsync(Guid workflowId, string name, string description, string tagsCsv, CancellationToken cancellationToken = default);
    Task<AutomationNodeView> SaveNodeAsync(Guid workflowId, AutomationNodeEditor editor, CancellationToken cancellationToken = default);
    Task MoveNodeAsync(Guid workflowId, Guid nodeId, double positionX, double positionY, CancellationToken cancellationToken = default);
    Task DeleteNodeAsync(Guid workflowId, Guid nodeId, CancellationToken cancellationToken = default);
    Task<AutomationConnectionView> AddConnectionAsync(Guid workflowId, Guid sourceNodeId, string sourceOutput, Guid targetNodeId, string targetInput = "main", CancellationToken cancellationToken = default);
    Task DeleteConnectionAsync(Guid workflowId, Guid connectionId, CancellationToken cancellationToken = default);
    Task<AutomationValidationResult> ValidateAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<int> PublishAsync(Guid workflowId, string changeSummary, CancellationToken cancellationToken = default);
    Task SetActiveAsync(Guid workflowId, bool active, CancellationToken cancellationToken = default);
    Task<AutomationWorkflowSnapshot?> GetPublishedSnapshotAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<AutomationWorkflowSnapshot?> GetSnapshotByVersionAsync(Guid workflowId, int versionNumber, CancellationToken cancellationToken = default);
    Task<AutomationExecutionView?> GetExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);

    // Failed executions across every workflow, most recent first - an admin currently has no
    // way to see this without opening each workflow's own Executions tab individually.
    Task<IReadOnlyList<AutomationRecentFailureView>> ListRecentFailuresAsync(int take = 20, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationWorkflowVersionSummary>> ListVersionsAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<AutomationWorkflowDiff> DiffVersionsAsync(Guid workflowId, int fromVersion, int toVersion, CancellationToken cancellationToken = default);

    // Replaces the live draft (AutomationNodes/AutomationConnections) with the target published
    // version's content. Does not rewrite already-published history - the workflow must be
    // Published again to make the restored draft live, same as a git revert rather than a
    // history rewrite.
    Task RollbackToVersionAsync(Guid workflowId, int targetVersion, string performedBy, CancellationToken cancellationToken = default);

    // Builds a brand-new workflow from a portable node/connection graph, minting fresh node and
    // connection identities so the result is fully independent of wherever the graph came from
    // (an uploaded export file or a workflow template). Shared by import and
    // IAutomationTemplateService.InstantiateAsync rather than duplicating the remap logic.
    Task<AutomationWorkflowView> CreateFromGraphAsync(
        string name,
        string description,
        IReadOnlyList<AutomationNodeView> nodes,
        IReadOnlyList<AutomationConnectionView> connections,
        string performedBy,
        CancellationToken cancellationToken = default);
}

public interface IAutomationExecutionService
{
    Task<AutomationExecutionView> ExecuteAsync(
        Guid workflowId,
        string inputJson = "{}",
        string mode = "Manual",
        Guid? retryOfExecutionId = null,
        // The chain of ancestor workflow ids currently executing, used only by
        // automation.subWorkflow's handler to reject self/mutual recursion and cap call depth -
        // every other caller (manual run, webhook, schedule, database trigger) leaves this null,
        // meaning "this is a root execution."
        IReadOnlySet<Guid>? subWorkflowChain = null,
        CancellationToken cancellationToken = default);

    Task<AutomationExecutionView> ResumeAsync(
        Guid executionId,
        string signalPort = "main",
        string? mergeFieldsJson = null,
        CancellationToken cancellationToken = default);

    Task<AutomationExecutionView> CancelAsync(Guid executionId, CancellationToken cancellationToken = default);

    Task<AutomationExecutionView> ResolveApprovalAsync(
        Guid executionId,
        bool approved,
        string? comment = null,
        CancellationToken cancellationToken = default);

    // Re-runs a Failed execution starting from its failed node instead of from scratch, reusing
    // the failed AutomationNodeExecution's persisted InputJson and whatever sibling/merge state
    // the execution preserved at the moment it failed (see AutomationExecutionService
    // .RunToCompletionAsync's catch block, which now checkpoints on failure instead of
    // discarding PendingStateJson). A node that failed mid-merge (one of several labeled inputs
    // still in flight) may not reconstruct perfectly - a known, documented v1 limitation.
    Task<AutomationExecutionView> RetryFromFailedNodeAsync(Guid executionId, CancellationToken cancellationToken = default);
}

public interface IAutomationNodeRegistry
{
    IReadOnlyList<AutomationNodeDefinition> ListDefinitions();
    AutomationNodeDefinition? Find(string typeKey, int version = 1);
    // workflowOwnerUsername is the workflow's own creator (AutomationWorkflow.CreatedBy),
    // resolved once per execution by AutomationExecutionService - not "whoever is currently
    // interacting with the app" (scheduled/webhook triggers have no live user at all). Only
    // consulted by node types that write to a resource with its own separate access model
    // (currently database.setRowProperty, against SentinelAccessService) - see
    // AutomationNodeRegistry.ExecuteSetRowPropertyAsync.
    Task<AutomationNodeRunResult> ExecuteAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? credentialJson,
        string? workflowOwnerUsername = null,
        // Ancestor workflow ids for the currently-running call chain - see
        // IAutomationExecutionService.ExecuteAsync's own doc comment. Only automation.subWorkflow
        // reads this.
        IReadOnlySet<Guid>? subWorkflowChain = null,
        // Every other node's last output this run, keyed by node Name (not Id - matches how
        // {{ $node("Name").json.path }} expressions address them). Only populated by
        // AutomationExecutionService; null when a node is unit-tested directly against the
        // registry, in which case $node(...) expressions simply resolve to empty.
        IReadOnlyDictionary<string, JsonElement>? nodeOutputsByName = null,
        CancellationToken cancellationToken = default);
}

public interface IAutomationTemplateService
{
    Task<IReadOnlyList<AutomationWorkflowTemplateSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<Guid> CreateFromWorkflowAsync(Guid workflowId, string name, string description, string performedBy, CancellationToken cancellationToken = default);
    Task<AutomationWorkflowView> InstantiateAsync(Guid templateId, string newWorkflowName, string performedBy, CancellationToken cancellationToken = default);
    Task DeleteTemplateAsync(Guid templateId, CancellationToken cancellationToken = default);
}

public interface IAutomationCredentialService
{
    Task<IReadOnlyList<AutomationCredentialSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<Guid> SaveAsync(Guid? id, string name, string typeKey, string credentialJson, string description = "", CancellationToken cancellationToken = default);
    Task<string?> GetDecryptedDataAsync(Guid credentialId, CancellationToken cancellationToken = default);
}

public interface IAutomationHttpClient
{
    Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default);
}

public interface IAutomationTriggerService
{
    Task<AutomationExecutionView?> TriggerWebhookAsync(string path, string inputJson, string? providedSecret, CancellationToken cancellationToken = default);
    Task<int> RunDueSchedulesAsync(CancellationToken cancellationToken = default);
    Task<int> ResumeDueWaitsAsync(CancellationToken cancellationToken = default);
    Task<AutomationExecutionView?> ResumeViaWebhookAsync(string token, string bodyJson, CancellationToken cancellationToken = default);

    // Fires every active workflow whose enabled "database.rowChangedTrigger" node targets
    // wikiDatabaseId. Returns the number of workflows triggered. Never throws for an
    // individual workflow's own execution failure (recorded on that workflow's
    // AutomationExecution instead) - a misconfigured or broken automation must not be able to
    // block the Sentinel row save that triggered it.
    Task<int> TriggerDatabaseRowChangedAsync(Guid wikiDatabaseId, string inputJson, CancellationToken cancellationToken = default);
}
