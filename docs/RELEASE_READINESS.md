# GWS Business Suite 1.0 release readiness

This document is the authoritative definition of **100% complete** for GWS Business Suite
1.0. Feature-specific documents describe delivered behavior and longer-term parity targets;
they do not override the release gates here.

## Scope boundary

GWS 1.0 is a production-ready business suite centered on the hosted ASP.NET Core/Blazor
application and a signed macOS client. Windows, iOS, and Android must continue to produce QA
artifacts, but store-ready releases are post-1.0 unless this contract is explicitly amended.

The release includes:

- the admin portal, authentication, public CMS sites, Content Studio, CRM, CJ operations,
  intelligence, Live Show, and deployment/health surfaces;
- Sentinel's versioned Notion-class v1 contract and its guarded Notion connector;
- SentinelGPT as a private, grounded GWS assistant with governed server-side actions;
- Growth Studio's first-party analytics and governed social-publishing release scope;
- durable Workflow Automation for approved GWS and HTTP integrations;
- a signed and notarized macOS package that hosts the canonical responsive web experience.

The following are explicitly outside GWS 1.0:

- character-level CRDT/OT co-editing;
- unrestricted Notion schema mutation or perfect translation of Notion formulas;
- session replay and heatmaps without a separate consent, redaction, and storage design;
- arbitrary workflow shell/code execution without a sandbox architecture;
- unreviewed community workflow packages;
- offline-native editing and store-ready Windows/iOS/Android distribution;
- a general claim that SentinelGPT outperforms every hosted AI model. SentinelGPT is judged
  by a versioned, GWS-specific evaluation suite.

## Evidence rules

A requirement is complete only when its named evidence exists. Backend implementation alone
does not prove a browser workflow, and a local test does not prove a deployed integration.

| Evidence class | Required proof |
| --- | --- |
| Automated | A repeatable test or validation command passes from a clean checkout |
| Browser | A Playwright journey passes at desktop and 390px where the workflow is responsive |
| Deployed | The journey passes against the production topology and records no secret material |
| Manual external | A controlled real-account success and controlled failure are recorded |
| Operational | Backup/restore, rollback, health, alerting, and incident steps are rehearsed |

Evidence expires when a later change materially affects the same workflow. Credentials,
private prompts, imported content, tokens, and personal data must never be embedded in release
reports.

## Priority and release rule

- **P0** blocks release because it protects authentication, data, secrets, recovery, or the
  ability to serve requests.
- **P1** is required product behavior for the approved GWS 1.0 contract.
- **P2** is valuable post-release work and does not contribute to the 1.0 score.

GWS 1.0 is 100% only when every P0 and P1 row passes. Scores cannot average around a failed
P0. An item marked `Not run` is incomplete, even when code for it exists.

## Current verified baseline

This table is a checkout snapshot, not a permanent claim. The release harness will replace
manual entries with generated evidence where practical.

| Check | Priority | Current evidence | Status |
| --- | --- | --- | --- |
| Release solution build | P0 | 0 warnings/errors on 2026-08-12 (Workflow Automation engine additions) | Pass |
| Full automated suite | P0 | 0 failed on 2026-08-12 local Release validation, via `verify-release.sh` (adds 16 new Workflow Automation cases to the 2026-08-11 baseline of 1098) | Pass |
| Direct/transitive NuGet vulnerability audit | P0 | No known vulnerable packages on 2026-08-11 | Pass |
| Docker Compose rendering | P0 | `docker compose config --quiet` on 2026-08-11 | Pass |
| SentinelGPT response-length UI | P1 | Corrected 2026-08-12: this row previously claimed "desktop and 390px Playwright smoke" evidence that does not exist anywhere in the repo or its git history - no test loads `SentinelGpt.razor` at all, and no test in the whole suite sets a 390px viewport (confirmed by a fresh audit). Only the backend response-budget plumbing is tested (`SentinelAiServiceTests.cs`, `SentinelGptGenerationCoordinatorTests.cs`) | Not run |
| HTTP security headers (CSP/X-Frame-Options/Referrer-Policy/Permissions-Policy/HSTS) | P0 | Confirmed present on `https://admin.gwsapp.net` response headers on 2026-08-11 | Pass |
| Disk-space health check | P0 | `DiskSpaceHealthCheck` unit-tested (path selection, missing-directory fallback, connection-string parsing) 2026-08-11; `/health/ready` returns Healthy in production with the check wired in | Local pass; deployed low-disk (Degraded/Unhealthy) branch not exercised, by construction |
| Operational failure alerting (email on background-job/backup/automation failure) | P1 | `OperationalAlertService` unit-tested (send, per-source cooldown throttling, opt-in-only, never-throws) on 2026-08-11 | Local pass; no controlled failure has been triggered against the deployed SMTP config |
| Operational data retention purge (automation/social-alert/live-show/app-gen/news/CJ-commission/podcast tables) | P1 | `OperationalDataRetentionService` unit-tested (per-table cutoffs, terminal-status-only automation purge, node-execution cascade, live-show file deletion) on 2026-08-11 | Local pass |
| CRM deal/pipeline board | P1 | `CrmService` deal create/update/stage-move/delete and dashboard cache-invalidation unit-tested on 2026-08-11 | Local pass; no browser journey yet |
| Workflow Automation engine additions (sub-workflows, failed-node retry, templates, import/export, version diff/rollback, `$node(...)` expression references, CRM/CMS/Growth action nodes) | P1 | 16 new `AutomationWorkflowTests` cases plus the existing suite unit-test each addition on 2026-08-12; full `verify-release.sh` run (build, audit, Compose, full suite) passed the same day | Local pass; no browser journey against the new UI (Save as template, Import/Export, Versions tab, Retry from failed node) yet; pinned/mock execution data and CJ/storage nodes remain unbuilt |
| SentinelGPT governed write tool (`propose_set_database_row_property`: authorization, structured input, preview/confirm, idempotency, audit evidence) | P1 | 5 new `SentinelAiServiceTests` cases (propose-without-writing, confirm-executes, decline-leaves-untouched, double-resolve rejected, access-denied) on 2026-08-12 | Local pass; no browser journey through the Confirm/Decline UI yet; this is one action, not the full "every enabled action" gate |
| Workflow Automation editor/lifecycle/credential additions (cron + multi-condition triggers, notify action, opt-in chaining, tags/duplicate, OAuth2 credential refresh, editor undo/redo/multi-select/copy-paste/minimap) | P1/P2 | 25 new `AutomationWorkflowTests`/`CronScheduleTests` cases on 2026-08-12; full `verify-release.sh` passed the same day | Local pass for everything service-layer; the editor UX items (undo/redo, multi-select, copy/paste, minimap) are Blazor code-behind with no automated coverage of any kind, consistent with every other Razor page in this app - no browser journey exists for any of it |
| Mandatory portal MFA | P0 | TOTP/recovery/replay tests plus local browser enrollment and returning-login journeys | Local pass; deployed acceptance required |
| Security audit ledger | P0 | Hash-chain, secret-metadata rejection, encrypted-network, account-event tests and local admin browser journey | Local pass; deployed acceptance required |
| Privacy and incident operations | P0 | Identity-gated subject export, one-month rights clock, retention preview, incident register, and 72-hour breach clock tests | Local pass; legal policy approval and deployed acceptance required |
| Empty and upgraded database migration rehearsal | P0 | Startup/migration compatibility tests apply the complete chain; production run [32599188395](https://github.com/granticusmaximus/GwsBusinessSuite/actions/runs/32599188395) created and verified fresh encrypted backup `gws-backup-20260822T212801366Z.gwsbackup` (`IsValid: true`), then round-tripped only its isolated restored database from `20260821195831_AddSupportTicketSatisfactionRating` to `20260821211756_AddSupportTicketSlaBreachAutomation`; internal readiness and all external checks passed on 2026-08-22 | Production-topology pass |
| Production liveness/readiness | P0 | `https://admin.gwsapp.net/health/live` and `/health/ready` returned 200 on 2026-08-11 | Pass |
| Backup plus Data Protection key restore | P0 | Encrypted authenticated archive, manifest, database/key/recording restore, migration, integrity, MFA, Sentinel, audit-chain, restored-secret, and tamper tests; deployment run [32528155116](https://github.com/granticusmaximus/GwsBusinessSuite/actions/runs/32528155116) created and verified a fresh production archive on the droplet on 2026-08-21 | Automated production create/verify pass; manual isolated-container browser verification, off-host backup confirmation, and external key escrow evidence still required |
| Deployment rollback | P0 | Rehearsal run [32538261980](https://github.com/granticusmaximus/GwsBusinessSuite/actions/runs/32538261980) verified a fresh encrypted backup, healthy `4092afc`, rollback to distinct runtime commit `50da93e`, preserved data volume, and real rollback readiness; run [32539176876](https://github.com/granticusmaximus/GwsBusinessSuite/actions/runs/32539176876) restored `4092afc` and passed internal plus external checks on 2026-08-22 | Production-topology pass |
| Real Notion sync and guarded write-back | P1 | Requires controlled workspace acceptance run | Not run |
| Real social publish success/failure | P1 | Requires controlled platform destinations | Not run |
| Live TURN relay from restricted network | P1 | Requires deployed network acceptance run | Not run |
| Signed/notarized macOS install | P1 | Requires release identity and clean-machine install | Not run |

## P0 release gates

### Security and privacy

- [ ] Every admin page and privileged endpoint enforces server-side authorization.
- [ ] Login throttling/lockout, secure cookies, proxy-aware HTTPS, CSRF, upload limits,
  open-redirect controls, and SSRF boundaries have automated coverage.
- [ ] Production secrets remain in server/platform secret stores and are absent from source,
  published artifacts, browser responses, logs, analytics, and AI context.
- [ ] Browser-origin inventory documents every intentional external request and its purpose.
- [ ] Public forms, analytics ingestion, public shares, webhooks, authentication, and AI entry
  points have appropriate abuse controls.
- [ ] Dependency and repository secret scans pass in CI.
- [ ] A repository-grounded threat model has no unresolved critical/high finding.

### Data, migrations, and recovery

- [ ] The complete migration chain applies to an empty SQLite database.
- [ ] The latest migration applies to a copy of the deployed database.
- [ ] A production-format backup contains a consistent database and matching Data Protection
  key ring.
- [ ] A restored isolated instance passes integrity, readiness, sign-in, Sentinel read, and
  encrypted connector-read checks.
- [x] Deployment rollback is rehearsed without losing post-migration data.

### Runtime and operations

- [ ] `/health/live` and `/health/ready` behave correctly in healthy and dependency-failure
  conditions.
- [ ] Structured logs and metrics identify authentication, AI, sync, automation, background
  job, and external-integration failures without recording private payloads.
- [ ] Production alerting and incident instructions are tested.
- [ ] Full Release build, automated suite, browser release suite, Compose validation, and
  package audit pass from the release commit.

## P1 product gates

### Core portal and publishing

- [ ] Authentication, navigation, user management, settings, and responsive shell journeys pass.
- [ ] Content Studio draft, review, revision diff, restore, article publication, media, and
  CMS Canvas journeys pass.
- [ ] Public CMS rendering, form submission, static export, SEO metadata, and comments pass.
- [ ] CRM, CJ, intelligence, and Docker health provide actionable success and failure states.
- [ ] Live Show broadcast, invite, TURN relay, recording, and replay pass when deployed.

### Sentinel and Notion

<!-- 2026-08-12 evidence audit (applies to every bullet below): only one Playwright test file
exists in the repo, WikiBlockEditorBrowserTests.cs, and it drives an isolated block-editor
harness, never the real routed Wiki.razor/SentinelGpt.razor pages. No test anywhere in the
codebase sets a 390px viewport, so the "Browser" evidence bar this doc itself defines (desktop
AND 390px) is not met by anything today, even where a Playwright test nominally exists. Per-bullet
notes below cite what's genuinely covered (Automated, and desktop-only Browser where it exists)
and name what isn't - "partially covered" everywhere, nothing here clears the bar to check yet. -->

- [ ] Page navigation, last-page restoration, breadcrumbs, working links, editing, autosave,
  history, templates, discussions, sharing, and responsive behavior pass.
  <!-- Automated only, and only for some: breadcrumb logic (SentinelTreeNavigationTests.cs),
  page/row history (PageRevisionServiceTests.cs), page/database/block templates
  (SentinelTemplateServiceTests.cs), discussion anchor logic (SentinelDiscussionAnchorRebaserTests.cs),
  and public-share access (SentinelAccessServiceTests.cs). Desktop-only Browser evidence for
  editing (WikiBlockEditorBrowserTests.cs broadly) and for link-click intent / selection-comment
  intent (same file). NOT COVERED by any test: page-to-page navigation, last-page restoration
  behavior itself, autosave, and responsive behavior at any viewport. -->
- [ ] Table and board databases support row creation, editing, drag/drop, views, formulas,
  relations, rollups, row pages, and history in browser tests.
  <!-- Desktop-only Browser evidence (WikiBlockEditorBrowserTests.cs) for row creation, editing,
  drag/drop, and Board/List/Table views. Formulas, relations, rollups, row pages (as their own
  page), and row history have real Automated/unit coverage (WikiDatabaseServiceTests.cs) but zero
  Browser evidence - this bullet explicitly asks for "in browser tests," which unit coverage
  doesn't satisfy. Relation-cell editing has no interactive UI yet (renders read-only). -->
- [ ] OAuth and manual-token connection paths work without accepting Notion credentials.
  <!-- FULLY COVERED at the Automated tier: NotionOAuthServiceTests.cs (authorize/exchange/refresh/
  disconnect) and NotionSyncServiceTests.cs (manual-token save/validate/replace). No UI path
  anywhere solicits a Notion username/password. No Browser evidence for the connect-button flow,
  but the bullet doesn't explicitly require it - closest to checkable of any bullet in this
  section, held back only by the section's shared lack of Browser coverage elsewhere. -->
- [ ] Discovery, selective sync, stable unchanged counts, imported blocks/files, covers/icons,
  typed properties, conflicts, archival, webhook refresh, and ZIP fallback pass against a
  controlled Notion workspace.
  <!-- STRUCTURALLY EXTERNAL - needs a real Notion workspace, tracked in
  docs/RELEASE_RUNBOOK_REMAINING.md. Every named sub-behavior already has extensive Automated
  (mocked-API) coverage in NotionSyncServiceTests.cs/NotionWebhookServiceTests.cs/
  SentinelWorkspaceImportServiceTests.cs, so this is ready to run the moment real credentials are
  available - not a code gap, a credentials gap. -->
- [ ] Explicit page/row write-back is authorized, confirmed, idempotent, and audited.
  <!-- Only "authorized" (SyncDirection/AllowTwoWayWrites gating) has test evidence
  (NotionSyncServiceTests.cs PushDatabaseRowAsync/PushDatabaseSchemaAsync tests). "Confirmed,"
  "idempotent," and "audited" are all NOT COVERED - no confirmation dialog wraps the one UI
  caller (Wiki.razor's PushPageAsync), no test exercises a repeat push for idempotency, and
  neither push method writes to SecurityAuditService. Distinct gap from the SentinelGPT
  propose_set_database_row_property tool below, which does have all four properties. -->

### SentinelGPT

<!-- 2026-08-12 evidence audit: no test anywhere loads SentinelGpt.razor - every UI-facing claim
below (streaming render, scrolling, keyboard send, bottom scroll) has zero test evidence
regardless of whether the code exists, per this doc's own "Not run is incomplete even when code
exists" rule. What's tested is backend/coordinator mechanics one layer down. This pass also found
and corrected a false evidence claim in the baseline table above ("SentinelGPT response-length
UI... Pass") that cited a Playwright test which does not exist anywhere in the repo or its git
history - flagging here since it's the same integrity question this whole section is about. -->

- [ ] Streaming, scrolling, keyboard send/newline, cancellation, response length,
  conversation reopening, bottom scroll, circuit recovery, and restart failure states pass.
  <!-- Automated backend-mechanics evidence only, for some: streaming chunks
  (SentinelAiServiceTests.cs StreamAsync test), cancellation gate and response-length plumbing
  (SentinelGptGenerationCoordinatorTests.cs), circuit recovery mechanism ("continue without a
  waiting caller," same file). NOT COVERED by any test: scrolling, keyboard send/newline, bottom
  scroll, and the restart-failure catch branch in SentinelGptGenerationCoordinator.cs (the code
  path exists but no test ever cancels the host's ApplicationStopping token to exercise it).
  Zero Browser evidence for any of these - no test opens the actual chat page. -->
- [ ] Web research, official Microsoft-source filtering, sanitized GWS grounding, approved
  memory, teacher-panel Deep mode, and model management pass success/failure tests.
  <!-- Strong Automated coverage for five of six: web research, MS-source filtering (including an
  explicit test that an impersonating non-Microsoft domain is excluded from citations), sanitized
  grounding (asserts a seeded secret/PII field never enters the prompt), approved memory, and Deep
  mode - all in SentinelAiServiceTests.cs, largely one comprehensive test plus skip-path tests.
  Model management has success-path coverage only (install/update-confirm, switch-to-installed);
  its explicit failure branch - reject a switch to a non-installed model - has no test despite the
  bullet asking for "success/failure." No Browser evidence for any of the six. -->
- [ ] A sanitized incremental repository index provides file/line citations without indexing
  secrets, build output, databases, binaries, or user-private content.
  <!-- Confirmed still not built anywhere in the codebase as of 2026-08-12 (repo-wide search for
  any repository/code-index service, empty). Not a test gap - a feature gap. -->
- [ ] A GWS evaluation suite measures correctness, grounding, citation validity, assumption
  correction, refusal boundaries, first-token latency, and total response time.
  <!-- Confirmed still not built anywhere in the codebase as of 2026-08-12. General HTTP
  request-latency aggregation infrastructure exists (PerformanceInfrastructureTests.cs) but is
  unrelated general-purpose telemetry, not a SentinelGPT-specific versioned eval harness - not
  partial credit toward this bullet. -->
- [ ] Every enabled action uses authorization, structured input, preview/confirmation where
  consequential, idempotency, audit evidence, and a confirmed result.
  <!-- 2026-08-12: the tool-calling loop's first write-capable tool,
  propose_set_database_row_property, now has all five properties with automated (unit) test
  evidence - see docs/SENTINELGPT_AGENT_SETUP.md's "Governed write actions" section. search_wiki
  and get_page (read-only) predate this and needed none of the five. Box stays unchecked: this is
  one action, not "every enabled action" as a category, and none of it has Browser-class evidence
  (a Playwright journey through the Confirm/Decline UI) yet. -->
- [ ] Production latency objectives are established at 30 seconds to first token and 120 seconds
  total for each of three warmed production samples capped at 64 output tokens; a published
  droplet run must still demonstrate `ObjectivesMet: true`.
  <!-- First production run (2026-08-22, run 32601134782, commit cba2e20) measured
  ObjectivesMet: false - first-token times 30751/95895/10585 ms against the 30000 ms objective
  (total times 35695/100274/17479 ms against 120000 ms DID pass on average). Likely confounded
  by running the probe immediately after a fresh app-container restart that itself included a
  new heavier startup seed step (EnsureUserGuidesInSentinelAsync, same deploy) rather than a
  genuine steady-state measurement - see docs/RELEASE_RUNBOOK_REMAINING.md's fuller note for the
  reasoning and the required clean re-run before this box can be honestly checked either way. -->

### Growth Studio

- [ ] First-party analytics, privacy signals, filters, realtime, acquisition, conversions,
  funnels, segments, cohorts, geography, and retention controls pass.
- [ ] CSV export, date comparison, annotations, and scheduled reports reconcile with totals.
- [ ] Revenue/ecommerce, entry/exit journeys, bot/referrer exclusions, and attribution imports
  have explicit data contracts and auditable behavior.
- [ ] Facebook, X, and LinkedIn OAuth/refresh, text/media publishing, previews, scheduling,
  approval, retry, engagement import, calendar, UTM, and inbox behavior pass controlled tests.

### Workflow Automation

- [ ] Draft/publish/version, graph editing, mapping, branching, joins, batching, retries, waits,
  approvals, cancellation, timeout, checkpoint, restart recovery, and evidence pass.
- [ ] Sub-workflows, partial execution, failed-node retry, templates, import/export,
  diff/restore, pinned data, and expression tooling pass.
  <!-- 2026-08-12: sub-workflows, failed-node retry, templates, import/export, diff/restore, and
  $node(...) expression references are built with automated (unit) test evidence - see the
  Workflow Automation engine additions row in the baseline table above. Still open: pinned/mock
  execution data, and Browser-class evidence for the new UI. Box stays unchecked per the evidence
  rules until both land. -->
- [ ] GWS nodes cover Sentinel, CMS, CRM, Growth/social, CJ, storage, and governed AI.
  <!-- 2026-08-12: CRM (setDealStage, saveContact), CMS (savePage), and Growth (publishSocialPost)
  action nodes now exist with automated test evidence. CRM Deal Stage Changed and CMS Page
  Published *trigger* nodes were added in the same pass (automated test evidence: cross-module
  trigger tests in AutomationWorkflowTests.cs) - closing the "no CRM/CMS/Growth trigger nodes" gap
  noted here previously. Still open: CJ and storage nodes, and a Growth/social trigger node
  (affiliate-conversion and security-audit-finding triggers were investigated and deliberately
  not built - no live write path exists for either in this codebase yet, so a trigger would be
  permanently unfireable; see docs/WORKFLOW_AUTOMATION.md). -->
- [ ] Credential isolation, rotation/audit, projects/RBAC, concurrency, retention, metrics, and
  OpenTelemetry gates pass.
  <!-- 2026-08-12: per-workflow access grants now exist (binary, not tiered - reuses Sentinel's
  own SentinelResourcePermission table; automated test evidence in SentinelAccessServiceTests.cs)
  plus unified security-audit-stream coverage for publish/activate/approval-resolution governance
  actions (automated test evidence in AutomationAuditTrailTests.cs). Still open: tiered
  View/Edit/Comment permissions (today's grant is full-access-or-nothing per workflow), projects,
  concurrency controls, retention/metrics, OpenTelemetry, and credential rotation *audit* (refresh
  itself already exists per the baseline table above). -->

### Native macOS client

- [ ] A signed and notarized package installs on a clean supported Mac.
- [ ] `open GWS`, authentication persistence, navigation, external links, downloads, uploads,
  camera/microphone, failure states, and update strategy pass.
- [ ] Untrusted origins cannot remain in the WebView and release URLs require HTTPS.
- [ ] Windows/iOS/Android QA artifact workflows remain green.

## P2 backlog

- Character-level CRDT/OT editing and multi-instance collaboration transport.
- Sentinel workspace graph visualization unless promoted into P1.
- Session replay and heatmaps after privacy architecture approval.
- Offline-native editing and signed store releases beyond macOS.
- Workflow code/shell nodes after sandbox design, and any reviewed community extension SDK.
- Broader competitor parity not represented by a named GWS user outcome.

## Execution order

`docs/RELEASE_RUNBOOK_REMAINING.md` is the current punch list of exactly what's left and who it
needs (droplet access, a real browser session, legal sign-off, or external-account credentials).

1. Generate the local/deployed release-acceptance harness and evidence report.
2. Close P0 security, privacy, recovery, migration, health, and rollback gates.
3. Verify real external integrations and convert failures into bounded fixes.
4. Complete SentinelGPT retrieval, evaluations, governed actions, and performance objectives.
5. Close remaining Sentinel workspace P1 gaps.
6. Complete Growth Studio P1 analytics and social publishing.
7. Complete Workflow Automation P1 reuse, operations, connectors, and governance.
8. Produce and verify the signed macOS release.
9. Run the complete release rehearsal and sign off every P0/P1 row.
