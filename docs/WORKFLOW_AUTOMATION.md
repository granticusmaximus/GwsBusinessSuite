# Workflow Automation

GWS Workflow Automation is an original, clean-room workflow system inspired by the
capabilities users expect from products such as n8n. It does not copy n8n source code,
UI assets, trademarks, node definitions, or enterprise-only implementation details.

## Architecture

- `AutomationWorkflow`, `AutomationNode`, and `AutomationConnection` store the editable graph.
- Every publish creates an immutable `AutomationWorkflowVersion` JSON snapshot.
- `AutomationExecution` and `AutomationNodeExecution` retain run and per-node evidence.
- `AutomationCredential` stores protected credential JSON; decrypted values never enter
  workflow snapshots or execution output.
- `IAutomationNodeRegistry` owns node metadata and handlers. Integrations add nodes through
  registration rather than editing the execution engine.
- `IAutomationWorkflowService` owns graph editing, validation, versioning, and activation.
- `IAutomationExecutionService` executes published snapshots, not a graph that may be edited
  during a run.

## Capability matrix

| Capability family | Foundation status | Expansion target |
| --- | --- | --- |
| Visual graph editor | Initial canvas, palette, inspector, connections, persisted positions | Marquee selection, copy/paste, undo/redo, minimap, sticky notes, keyboard command bar |
| Workflow lifecycle | Draft editing, validation, immutable publish versions, activate/deactivate | Tags, folders/projects, sharing roles, workflow history diff/restore, import/export/templates |
| Execution engine | Deterministic DAG execution, branching, retries, continue-on-fail, per-node evidence | Loops, joins, sub-workflows, durable waits/resume, partial execution, retry from failure |
| Core nodes | Manual Trigger, Set Fields, If, HTTP Request | Webhook, Schedule, Merge, Loop, Wait, Code, Execute Workflow, Respond to Webhook |
| Data mapping | JSON items and `{{ $json.path }}` expressions | Full expression editor, node references, item linking, binary data, pinned/mock data |
| Credentials | Protected credential records and credential references | OAuth2 refresh, credential types, sharing, external secret stores, rotation/audit |
| Operations | Run list, node logs, timestamps, errors, outputs | Filtering, retention/pruning, cancel, concurrency controls, metrics, OpenTelemetry |
| Scale and reliability | In-process execution with durable records | Queue transport, independent workers, webhook processors, leader election, Postgres |
| AI automation | Registry can host AI nodes | Agents, tools, memory, vector stores, model credentials, human approval nodes |
| Governance | Admin-only boundary and immutable version snapshots | Projects/RBAC, audit stream, environment promotion, source control, security audit |
| Integrations | Extensible C# node handler contract | First-party connectors, community package SDK, node versioning and compatibility policy |

The system should reach capability parity progressively. “Parity” means equivalent user
outcomes inside GWS Business Suite, not a pixel-identical clone or reuse of restricted code.

## Safety rules

- Workflows never store plaintext secrets in graph JSON.
- Server-side authorization remains authoritative; hiding a UI control is not authorization.
- HTTP nodes reject non-HTTP(S) destinations and use `IHttpClientFactory`.
- Code/shell nodes require a separate sandbox design and are not enabled by this foundation.
- Published versions are immutable so an execution can always explain what it ran.
