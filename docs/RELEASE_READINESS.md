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
| Release solution build | P0 | 0 warnings/errors on 2026-07-31 | Pass |
| Full automated suite | P0 | 811 passed, 0 failed on 2026-07-31 | Pass |
| Direct/transitive NuGet vulnerability audit | P0 | No known vulnerable packages on 2026-07-31 | Pass |
| Docker Compose rendering | P0 | `docker compose config --quiet` on 2026-07-31 | Pass |
| SentinelGPT response-length UI | P1 | Authenticated desktop and 390px Playwright smoke on 2026-07-31 | Pass |
| Empty and upgraded database migration rehearsal | P0 | Must be rerun by release harness | Not run |
| Production liveness/readiness | P0 | `https://admin.gwsapp.net/health/live` and `/health/ready` returned 200 on 2026-07-31 | Pass |
| Backup plus Data Protection key restore | P0 | Requires current restore rehearsal | Not run |
| Deployment rollback | P0 | Requires production-topology rehearsal | Not run |
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
- [ ] Deployment rollback is rehearsed without losing post-migration data.

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

- [ ] Page navigation, last-page restoration, breadcrumbs, working links, editing, autosave,
  history, templates, discussions, sharing, and responsive behavior pass.
- [ ] Table and board databases support row creation, editing, drag/drop, views, formulas,
  relations, rollups, row pages, and history in browser tests.
- [ ] OAuth and manual-token connection paths work without accepting Notion credentials.
- [ ] Discovery, selective sync, stable unchanged counts, imported blocks/files, covers/icons,
  typed properties, conflicts, archival, webhook refresh, and ZIP fallback pass against a
  controlled Notion workspace.
- [ ] Explicit page/row write-back is authorized, confirmed, idempotent, and audited.

### SentinelGPT

- [ ] Streaming, scrolling, keyboard send/newline, cancellation, response length,
  conversation reopening, bottom scroll, circuit recovery, and restart failure states pass.
- [ ] Web research, official Microsoft-source filtering, sanitized GWS grounding, approved
  memory, teacher-panel Deep mode, and model management pass success/failure tests.
- [ ] A sanitized incremental repository index provides file/line citations without indexing
  secrets, build output, databases, binaries, or user-private content.
- [ ] A GWS evaluation suite measures correctness, grounding, citation validity, assumption
  correction, refusal boundaries, first-token latency, and total response time.
- [ ] Every enabled action uses authorization, structured input, preview/confirmation where
  consequential, idempotency, audit evidence, and a confirmed result.
- [ ] Production latency objectives are established from actual droplet measurements and met.

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
- [ ] GWS nodes cover Sentinel, CMS, CRM, Growth/social, CJ, storage, and governed AI.
- [ ] Credential isolation, rotation/audit, projects/RBAC, concurrency, retention, metrics, and
  OpenTelemetry gates pass.

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

1. Generate the local/deployed release-acceptance harness and evidence report.
2. Close P0 security, privacy, recovery, migration, health, and rollback gates.
3. Verify real external integrations and convert failures into bounded fixes.
4. Complete SentinelGPT retrieval, evaluations, governed actions, and performance objectives.
5. Close remaining Sentinel workspace P1 gaps.
6. Complete Growth Studio P1 analytics and social publishing.
7. Complete Workflow Automation P1 reuse, operations, connectors, and governance.
8. Produce and verify the signed macOS release.
9. Run the complete release rehearsal and sign off every P0/P1 row.
