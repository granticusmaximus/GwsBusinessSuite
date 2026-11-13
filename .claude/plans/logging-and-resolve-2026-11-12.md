# Logging, issues and Resolve with SentinelGPT (12-11-26)

Goal: any problem in the app (server, browser, Mac app) can be traced end to end from one place,
grouped into issues, explained, and, where safe, resolved with one approval. Agreed with Grant
on 12-11-26.

Starting point: Serilog writes text to the console and `/app/data/logs/gwssuite-*.log`
(14 files, 50 MB each). The files are text only, nothing links events across browser, circuit and
server, browser JS errors are barely captured, the Mac app's logs stay on the Mac, and production
doesn't know its own commit (the image is built without `.git`).

## Phase 1: capture and see (structured logs, request IDs, blind spots, issues, logs page)

### Capture

1. A JSON log (`gwssuite-*.clef`, Serilog compact JSON) alongside the text file. Every event
   carries `Release` (the commit), `Origin` (server, browser, mac-app), the Activity
   `TraceId`/`SpanId`, ASP.NET's `RequestId`/`RequestPath`, and on circuits `CircuitId` and
   `UserName`.
2. The production image knows its commit: deploy passes `SOURCE_COMMIT`, the Dockerfile
   publishes with `SourceRevisionId` (the shell strip shows it too).
3. Browser errors: `js/client-errors.js` reports `error` and `unhandledrejection` on admin pages
   (deduplicated, capped per page) to `POST /api/diagnostics/client`, with the document's trace id
   from a `gws-trace` meta tag, so a client error links to the request that served the page.
4. Mac app: map-bridge failures and likely-missed pin clicks are forwarded through the page
   (`window.gwsClientLog.report`) as `Origin=mac-app`, as well as written to the local log.
5. Redaction (emails, bearer tokens, token/key/password query values) in the Application layer,
   applied to everything clients send; Phase 2 reuses it before anything reaches a model.
6. Background job runs get their own trace (an Activity per run) so a job's lines group together.

### Issues and the logs page

1. Warnings and errors are copied to a SQLite `LogIssue`/`LogOccurrence` store: fingerprint =
   exception type + first app frame + message template. Count, first/last seen, release first
   seen, last few occurrences with their trace ids. Retention bounded.
2. `/admin/logs` (Admin only): issues list (new since deploy, spiking, by origin), an issue page
   with occurrences and stack, a trace timeline (every event with one TraceId across origins),
   filters by user, page, origin, release.
3. "Copy for Claude": the issue's evidence bundle (redacted) as Markdown.

## Phase 2: Resolve

1. Known causes, closed by rule with no AI: blocked SMTP, `ServerAiUnavailableException`,
   `RefusedHostPause`, congress.gov `DEMO_KEY` limits. Marked "expected" with the reason.
2. Explain: SentinelGPT (on the Admin's own Mac via the relay, not `ForSharedWork`, since logs
   hold personal data) gets the bundle plus the AGENTS.md known traps and returns likely cause,
   confidence, fix and files. SentinelGPT tools: search logs, open an issue, follow a trace.
3. Operational fixes from a fixed list in code (re-run a job, retry a feed, clear a cache,
   re-sync a relay, restart a stuck background service): previewed, approved by Grant, written to
   the security audit ledger. The model only picks from the list.
4. Code fixes on the Mac: worktree from `origin/main`, a failing test from the evidence, the fix,
   `verify-release.sh`, diff shown; approval commits to a branch. Never pushes. Hard cases go to
   Claude Code headless (`claude -p`) with the same bundle and the same approval.
5. Closing the loop: an issue that doesn't recur for N days of traffic after the fix's release
   closes itself; one that recurs reopens as "regressed in <release>".
6. Daily digest: new and spiking issues since the last deploy, summarised.

## Progress

- 12-11-26: plan agreed; Grant merged it into two phases.
- 12-11-26: Phase 1 built and checked live on a local copy (laptop and phone):
  - Capture: JSON log beside the text log (`SerilogBootstrapper`), Origin/Release enrichment,
    `UseUserLogContext`, circuit CircuitId/UserName (`CircuitDiagnosticsHandler`), commit in the
    image (`SOURCE_COMMIT` build arg), `js/client-errors.js` + `POST /api/diagnostics/client`
    (`DiagnosticsEndpointExtensions`), Mac map failures and near-miss clicks forwarded
    (`MacNativeOverwatchBridge.Report`), redaction (`LogRedactor`).
  - Issues: `LogIssue`/`LogOccurrence` (migration `AddLogIssues`), `LogIssueSink` ->
    `LogIssueIngestService` -> `LogIssueService`; `JsonLogTraceReader`; `/admin/logs` with trace
    view and Copy for Claude (`LogIssueBundle`). Platform nav link. Guide: PLATFORM_OPERATIONS.
  - Not done: per-run trace ids for the 45 background services (item 6). Their lines already group
    by SourceContext; add an Activity per run when a job's story needs it.
- Next: Phase 2, starting with known causes (rule-closed) and Explain with SentinelGPT.
- 13-11-26 Phase 2 (part):
  - 1 DONE: KnownCauses (ServerAiUnavailable, refused sites, blocked SMTP, congress.gov DEMO_KEY):
    new and open issues matching are marked expected with the reason (LogIssueService.RecordAsync).
  - 2 DONE: "Explain with SentinelGPT" on the issue page: the redacted bundle + known traps to the
    app's own model (Mac first, AI droplet second; never ForSharedWork), answer marked as AI.
  - 5 DONE: open issues quiet 7 days close themselves (PruneAsync); recurrence reopens (existing).
  - NOT DONE: 3 operational fixes and 4 code fixes (need a preview/approval flow and the audit
    ledger; they act on production and git), 6 daily digest (bell wiring), SentinelGPT log tools.
