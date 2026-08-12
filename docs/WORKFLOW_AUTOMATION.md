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
- Execution state (the remaining node queue and merge buffers) is checkpointed to
  `AutomationExecution.PendingStateJson` after every node step, so a `Wait`/`Approval` pause or
  an app restart mid-run can both resume from the exact point they left off, against the
  workflow version the execution started on (`IAutomationWorkflowService.GetSnapshotByVersionAsync`),
  not whichever version is newest at resume time.
- `AutomationResumeBackgroundService` sweeps for executions whose wait time has elapsed and for
  `Running` executions whose heartbeat has gone stale (an orphaned run from a crashed process),
  resuming both through the same `IAutomationExecutionService.ResumeAsync` path.
- `database.setRowProperty` (2026-08-01) is the write-back half of `database.rowChangedTrigger`
  - a workflow can now both react to and update a Sentinel database row. Its `IWikiDatabaseService`
  dependency is resolved lazily from the execution scope's `IServiceProvider`
  (`AutomationNodeRegistry`'s constructor takes it, not an eager parameter) because
  `WikiDatabaseService` depends on `IAutomationTriggerService`, which depends on
  `IAutomationExecutionService`, which depends back on this registry - an eager dependency
  would be circular. The write-back always saves as actor `"automation-engine"`, and
  `WikiDatabaseService.SaveRowAsync` never re-fires `database.rowChangedTrigger` for that
  actor, so this node can never chain into an automation loop (including a workflow
  re-triggering itself) - the tradeoff is that no automation may currently chain off another
  automation's database write through this trigger, full stop.
  Building this node's test surfaced and fixed a real bug: `AutomationExecutionService.ExecuteAsync`'s
  mode-to-trigger-type switch had no case for `AutomationExecutionModes.DatabaseTrigger`, so
  every database-row-changed execution silently started from `core.manualTrigger` (usually
  disconnected) instead of `database.rowChangedTrigger` - the Execution record showed success,
  but the workflow's actual configured logic downstream of the database trigger never ran. Fixed
  by adding the missing switch arm.

## Capability matrix

| Capability family | Foundation status | Expansion target |
| --- | --- | --- |
| Visual graph editor | Initial canvas, palette, inspector, connections, persisted positions, undo/redo (node and connection deletion), ctrl/shift-click multi-select, copy/paste (selected nodes plus their interconnections), minimap overview | Marquee/rubber-band selection, synchronized multi-node drag, sticky notes, keyboard command bar |
| Workflow lifecycle | Draft editing, validation, immutable publish versions, activate/deactivate, workflow history diff/restore, import/export/templates, tags (surfaced as a list-view filter; act as lightweight folders for browsing), duplicate | Dedicated folder/project hierarchy, sharing roles |
| Execution engine | Deterministic DAG execution, branching, labeled-input joins, multi-item fan-out, batching, retries, continue-on-fail, per-node evidence, checkpointed frontier with crash recovery, cooperative cancellation, per-node timeouts, sub-workflows (`automation.subWorkflow`, depth-capped at 10, child must complete synchronously), retry from a failed node, opt-in downstream automation chaining (`AllowDownstreamAutomationTriggers`, off by default) | Partial execution beyond retry-from-node (e.g. resume mid-merge exactly) |
| Core nodes | Manual/Webhook/Schedule (interval or cron)/Database Row Changed (optionally multi-condition) triggers; Set Fields, If, HTTP Request, Split Out, Batch, Merge, Limit, Sort, Remove Duplicates, Template, Date & Time, No Operation, Stop and Error, Wait, Approval, Set Database Row Property, Add Database Row, Execute Workflow, Notify (email), CRM Set Deal Stage/Save Contact, CMS Save Page, Growth Publish Social Post | Code, Respond to Webhook, CJ nodes, storage nodes, CRM/CMS/Growth *trigger* nodes |
| Data mapping | JSON items, `{{ $json.path }}` expressions, and `{{ $node("Name").json.path }}` references to any earlier node's last output this run (persisted through checkpoint/resume) | Item linking, binary data, pinned/mock data, a full expression editor (operators/functions/array indexing) |
| Credentials | Protected credential records, credential references, OAuth2 refresh/rotation for `oauth2`-type credentials (manual "Refresh" action plus a 15-minute background sweep for anything expiring within an hour) | Credential types beyond `httpHeader`/`oauth2`/generic, sharing, external secret stores, rotation audit trail |
| Operations | Run list, node logs, timestamps, errors, outputs, cancel, resume, approve/reject, retry from failed node | Filtering, retention/pruning, concurrency controls, metrics, OpenTelemetry |
| Scale and reliability | In-process execution with durable records | Queue transport, independent workers, webhook processors, leader election, Postgres |
| AI automation | Qwen/DeepSeek adviser nodes, SentinelGPT synthesis, human-approved learning memory | Tool calling, embeddings/vector search, evaluation suites, model credentials |
| Governance | Admin-only boundary and immutable version snapshots | Projects/RBAC, audit stream, environment promotion, source control, security audit |
| Integrations | Extensible C# node handler contract | First-party connectors, community package SDK, node versioning and compatibility policy |

The system should reach capability parity progressively. “Parity” means equivalent user
outcomes inside GWS Business Suite, not a pixel-identical clone or reuse of restricted code.

## Clean-room product research

The July 2026 design review used public product documentation as behavioral research only.
No vendor source, screenshots, icons, templates, names, or proprietary node schemas are
embedded in GWS. General workflow concepts and the GWS adaptations are:

| Public reference | General concept | GWS adaptation |
| --- | --- | --- |
| [n8n workflow and flow-logic documentation](https://docs.n8n.io/flow-logic/) | Item streams, data mapping, branches, loops, waits, sub-workflows, run inspection | Typed node registry, JSON item streams, immutable published graphs, per-node evidence |
| [Node-RED concepts](https://nodered.org/docs/user-guide/concepts) | Palette/workspace/sidebar model, message passing, scoped context, reusable subflows | GWS visual canvas and future workflow/node/global variable scopes |
| [Zapier Paths and filters](https://help.zapier.com/hc/en-us/articles/8496180919949-Filter-and-path-rules-in-Zap-workflows) | Business-friendly conditions, paths, delays, test records, human review | Condition builder, durable timers, pinned test data, approval nodes |
| [Power Automate cloud flows](https://learn.microsoft.com/power-automate/overview-cloud) | Connector-oriented triggers/actions and approval-centered business processes | First-party GWS connectors and auditable approval tasks |
| [Temporal workflows](https://docs.temporal.io/workflows) | Durable event history, deterministic recovery, timers, signals, child workflows | Persisted execution checkpoints and resumable background workers |
| [GitHub Actions workflows](https://docs.github.com/actions/concepts/workflows-and-actions/workflows) | Reusable workflows, concurrency controls, environments, run cancellation and artifacts | Sub-workflows, concurrency keys, environment promotion, output artifacts |

## Delivery sequence

1. **Item processing and graph composition:** multi-item outputs, split, batch, merge,
   transforms, labeled inputs, branches, and explicit failure nodes.
2. **Durability:** persisted node queue/checkpoints, Wait Until, webhook resume, approval
   tasks, cancellation, timeout policies, and safe restart recovery.
3. **Reuse and testing:** execute-workflow nodes, input/output contracts, pinned data,
   partial runs, retry from a failed node, templates, import/export, diff, and restore.
4. **Editor productivity:** typed property controls, data browser, expression autocomplete,
   undo/redo, copy/paste, multi-select, auto-layout, minimap, sticky notes, and command search.
5. **Connectors:** GWS CMS, CRM, CJ, email, Ollama/AI, database, storage, calendar, social,
   analytics, and a versioned connector development contract.
6. **Production scale and governance:** worker queues, leases, concurrency keys, retention,
   OpenTelemetry, RBAC/projects, audit events, secret rotation, and environment promotion.

## Safety rules

- Workflows never store plaintext secrets in graph JSON.
- Server-side authorization remains authoritative; hiding a UI control is not authorization.
- HTTP nodes reject non-HTTP(S) destinations and use `IHttpClientFactory`.
- Code/shell nodes require a separate sandbox design and are not enabled by this foundation.
- Published versions are immutable so an execution can always explain what it ran.
