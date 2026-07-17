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
    Task<AutomationExecutionView?> GetExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);
}

public interface IAutomationExecutionService
{
    Task<AutomationExecutionView> ExecuteAsync(
        Guid workflowId,
        string inputJson = "{}",
        string mode = "Manual",
        Guid? retryOfExecutionId = null,
        CancellationToken cancellationToken = default);
}

public interface IAutomationNodeRegistry
{
    IReadOnlyList<AutomationNodeDefinition> ListDefinitions();
    AutomationNodeDefinition? Find(string typeKey, int version = 1);
    Task<AutomationNodeRunResult> ExecuteAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? credentialJson,
        CancellationToken cancellationToken = default);
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
}
