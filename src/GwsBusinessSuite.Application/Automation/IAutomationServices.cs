using System.Text.Json;

namespace GwsBusinessSuite.Application.Automation;

public interface IAutomationWorkflowService
{
    Task<IReadOnlyList<AutomationWorkflowSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<AutomationWorkflowView?> GetAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<AutomationWorkflowView> CreateAsync(string name, string description = "", CancellationToken cancellationToken = default);

    // Permanently removes the workflow. AutomationNode/AutomationConnection/
    // AutomationWorkflowVersion/AutomationExecution (and AutomationNodeExecution beneath it) all
    // cascade-delete via FK configuration in ApplicationDbContext; SentinelResourcePermission and
    // SentinelPublicShare grants referencing this workflow (loosely-typed TargetId, no real FK -
    // the same shape shared with Sentinel pages/databases) are cleaned up explicitly. No-op if
    // the workflow no longer exists.
    Task DeleteWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default);

    // Clones a workflow's current live draft (not its published history) into a brand-new
    // workflow with fresh node/connection ids - built directly on CreateFromGraphAsync below,
    // same "portable graph + fresh identity" pattern as templates and import.
    Task<AutomationWorkflowView> DuplicateAsync(Guid workflowId, string newName, string performedBy, CancellationToken cancellationToken = default);
    Task UpdateMetadataAsync(Guid workflowId, string name, string description, string tagsCsv, CancellationToken cancellationToken = default);
    Task<AutomationNodeView> SaveNodeAsync(Guid workflowId, AutomationNodeEditor editor, CancellationToken cancellationToken = default);
    Task MoveNodeAsync(Guid workflowId, Guid nodeId, double positionX, double positionY, CancellationToken cancellationToken = default);
    Task DeleteNodeAsync(Guid workflowId, Guid nodeId, CancellationToken cancellationToken = default);
    Task<AutomationConnectionView> AddConnectionAsync(Guid workflowId, Guid sourceNodeId, string sourceOutput, Guid targetNodeId, string targetInput = "main", CancellationToken cancellationToken = default);
    Task DeleteConnectionAsync(Guid workflowId, Guid connectionId, CancellationToken cancellationToken = default);
    Task<AutomationValidationResult> ValidateAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<int> PublishAsync(Guid workflowId, string changeSummary, CancellationToken cancellationToken = default);
    Task SetActiveAsync(Guid workflowId, bool active, CancellationToken cancellationToken = default);

    // Opt-in escape hatch for the documented database.setRowProperty/database.addRow
    // no-chaining default - see AutomationWorkflow.AllowDownstreamAutomationTriggers's own doc
    // comment for the exact mechanism.
    Task SetAllowDownstreamAutomationTriggersAsync(Guid workflowId, bool allow, CancellationToken cancellationToken = default);
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

    // The only workflow read exposed to anonymous public-share viewers - see
    // AutomationPublicStatusView's own doc comment for exactly what is and isn't included.
    Task<AutomationPublicStatusView?> GetPublicStatusAsync(Guid workflowId, int take = 10, CancellationToken cancellationToken = default);
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
        // Time-travel replay (ReplayAsync below) - when true, every node with a real external
        // side effect (per IAutomationNodeRegistry.HasRealSideEffect) is never actually run;
        // its output is substituted from historicalOutputByNodeId instead, and downstream
        // automation chaining is force-disabled regardless of the workflow's own
        // AllowDownstreamAutomationTriggers setting, so a dry run truly has zero side effects.
        bool isDryRun = false,
        IReadOnlyDictionary<Guid, string>? historicalOutputByNodeId = null,
        // Overrides the trigger node type normally derived from mode - ReplayAsync uses this so
        // the current graph is entered from the same trigger type the original run started from,
        // even though mode is "Replay" rather than the original Manual/Webhook/Schedule/etc.
        string? triggerTypeKeyOverride = null,
        CancellationToken cancellationToken = default);

    Task<AutomationExecutionView> ResumeAsync(
        Guid executionId,
        string signalPort = "main",
        string? mergeFieldsJson = null,
        CancellationToken cancellationToken = default);

    Task<AutomationExecutionView> CancelAsync(Guid executionId, CancellationToken cancellationToken = default);

    // Re-runs a past execution's exact recorded trigger input against the CURRENT published
    // graph as a sandboxed dry run - lets an author see what today's logic would do differently
    // without any real side effects. Rejects executions that paused on a Wait/Approval node (no
    // recorded "what resumed it" data to replay from) and any node with a real side effect that
    // wasn't part of the original run (nothing to simulate it with) - both fail with a clear
    // message rather than guessing.
    Task<AutomationExecutionView> ReplayAsync(Guid sourceExecutionId, CancellationToken cancellationToken = default);

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
        // AutomationWorkflow.AllowDownstreamAutomationTriggers for the node's own workflow -
        // only consulted by database.setRowProperty/database.addRow to pick their write actor.
        bool allowDownstreamTriggers = false,
        // Time-travel replay - see IAutomationExecutionService.ExecuteAsync's own doc comment.
        // When true and HasRealSideEffect(node.TypeKey, node.TypeVersion) is true, this node is
        // never actually run; its output is substituted from historicalOutputByNodeId[node.Id],
        // throwing a clear error if no recorded output exists for it.
        bool isDryRun = false,
        IReadOnlyDictionary<Guid, string>? historicalOutputByNodeId = null,
        CancellationToken cancellationToken = default);

    // True for any node type with a real external side effect (HTTP call, CRM/CMS/Sentinel
    // write, email send, AI model call) - the set that time-travel replay must never actually
    // run during a dry run. Combines each definition's own IsIdempotent tag (already meant for
    // "safe to run again") with every "ai.*" node, which IsIdempotent doesn't cover since that
    // tag predates this feature and those nodes call a real Ollama model (and
    // ai.saveApprovedLesson writes to Sentinel).
    bool HasRealSideEffect(string typeKey, int version = 1);
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

    // Rotates an oauth2-type credential's access/refresh tokens against its own stored
    // tokenEndpoint, mirroring NotionOAuthService.RefreshAsync's pattern generically for any
    // provider rather than being Notion-specific. Requires the credential's decrypted JSON to
    // contain refreshToken and tokenEndpoint (clientId/clientSecret optional, sent as HTTP Basic
    // auth when present). Returns true if refreshed, false if the credential isn't an
    // oauth2-type credential or has no refresh token stored.
    Task<bool> RefreshOAuthCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default);

    // Every oauth2-type credential whose stored expiresAt is within `within` of now (or has no
    // expiresAt at all, since some providers never report one and must be refreshed on a fixed
    // cadence instead) - used by the background rotation sweep. Returns how many were refreshed.
    Task<int> RefreshExpiringOAuthCredentialsAsync(TimeSpan within, CancellationToken cancellationToken = default);
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

    // Same shape as TriggerDatabaseRowChangedAsync, for CRM/CMS domain events instead of
    // Sentinel database rows. Fires every active workflow with an enabled
    // "crm.dealStageChangedTrigger" node, optionally filtered by that node's toStage parameter.
    Task<int> TriggerCrmDealStageChangedAsync(string stage, string inputJson, CancellationToken cancellationToken = default);

    // Fires every active workflow with an enabled "cms.pagePublishedTrigger" node. Callers fire
    // this only on a draft-to-published transition, not on every page save.
    Task<int> TriggerCmsPagePublishedAsync(Guid siteId, string inputJson, CancellationToken cancellationToken = default);

    // Fires every active workflow with an enabled "sentinel.chatPromptSubmittedTrigger" node,
    // immediately after a user sends a chat message to SentinelGPT (see
    // SentinelGptGenerationCoordinator). The caller is expected to invoke this fire-and-forget,
    // wrapped in OllamaWorkloadScheduler.UseBackgroundPriority(), so a slow or misconfigured
    // subscriber workflow here can never delay or block the chat response the user is waiting
    // on - same non-blocking contract as every other trigger method on this interface, just
    // enforced by priority instead of by not awaiting (the chat response path never calls this
    // at all, so there is nothing to await there in the first place).
    Task<int> TriggerSentinelChatPromptSubmittedAsync(string prompt, Guid? conversationId, CancellationToken cancellationToken = default);

    // Fires every active workflow with an enabled "support.ticketCreatedTrigger" node,
    // immediately after a new support ticket is opened (either side). Same non-blocking
    // contract as every other trigger method here - callers wrap this in a try/catch so a
    // misconfigured subscriber workflow can never prevent the ticket itself from saving.
    Task<int> TriggerSupportTicketCreatedAsync(
        Guid ticketId, string subject, string contactName, string priority, CancellationToken cancellationToken = default);

    // Same shape, for an enabled "support.ticketRepliedTrigger" node, fired after any reply
    // (Contact or Staff) is added to an existing ticket.
    Task<int> TriggerSupportTicketRepliedAsync(
        Guid ticketId, string authorType, string authorName, string body, CancellationToken cancellationToken = default);

    Task<int> TriggerSupportTicketSlaBreachedAsync(
        Guid ticketId, string subject, string contactName, string priority, string breachType,
        DateTimeOffset dueAt, CancellationToken cancellationToken = default);

    // Fires every active workflow with an enabled "cms.formSubmittedTrigger" node, immediately
    // after a public form widget submission is saved. Same non-blocking contract as every other
    // trigger method here - the caller wraps this in a try/catch so a misconfigured subscriber
    // workflow can never prevent the submission itself from saving.
    Task<int> TriggerCmsFormSubmittedAsync(Guid siteId, string inputJson, CancellationToken cancellationToken = default);
}
