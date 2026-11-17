# Open work review and newsroom beats (16-11-26)

Grant requested a review of open plans and implementation of remaining work. Started from a
clean checkout at `d691dc6e`, with `HEAD..origin/main` equal to zero after fetching.

## Review

The shared tracker contains 65 plans and historical handoffs. Unchecked boxes alone are not a
reliable backlog: later progress entries and current code supersede several of them.

| Area | Remaining work / disposition |
| --- | --- |
| World News trust and newsroom | N5-N10 UI is implemented (N6 onward unvalidated); N9 is historical approval records, not a CMS gate. N11-N15 and B10 remain. Extend the existing backend, not a second desk/store. |
| Logging and Resolve | Operational fixes and code-fix approval/audit flows, daily digest, agent log tools and per-job traces remain in the logging plan. Production-changing actions need the planned approval boundary. |
| World News / Overwatch expansion | Unit 1 is still unchecked, but regional outlets and the two-outlet operational threshold already exist. Reconcile the remaining adaptive-threshold requirement before changing trust gates. Units 2-6 are recorded complete. |
| Overwatch camera availability | Provider policy/capability persistence and the dependent UX are still unchecked. Source terms and capture permissions are prerequisites. |
| Mac acceptance | Native map/renderer plans retain device acceptance items. Windows cannot verify installed Mac app behavior. Later MapKit work supersedes parts of the old WebKit recovery proposals. |
| Older delivery plans | Known gaps, app-wide UX, mail, calendar and the ten Overwatch features have completion entries. Optional proposals, unavailable providers and historical checklists are not automatically new implementation requirements. |

Primary references: `world-news-trust-and-crawler-2026-11-13.md`,
`logging-and-resolve-2026-11-12.md`, `world-news-overwatch-expansion-2026-11.md`,
`overwatch-camera-availability-and-world-news-2026-11-09.md`,
`droplet-security-and-overwatch-ten-2026-11-13.md`, and
`sync-and-overwatch-closure-2026-11-10.md`.

## Implementation unit: newsroom beats (N5)

Expose the existing per-person region/topic beats and bounded story queue on News desk.
Reuse the pipeline's beat alerts and bell/push event; preserve its corroboration and hold gates.
Regions retain the existing source-country meaning and must not imply event geolocation.
Fix any beat ownership or alert-retry defects exposed by enabling this workflow.

No schema change, new service, network source, publishing action or remote Git write.
Other newsroom features remain separate units; this does not close N5-N15 as a group.

## Progress

- [x] Fetch and confirm current, clean source; inspect plan inventory and relevant implementations.
- [x] Verify N5 backend/pipeline exists and the desk has no beat management/queue UI.
- [x] Add beat controls, personal queue and explicit loading/error states.
- [x] Test ownership, validation, matching/order and deferred alert eligibility.
- [x] Update the World News user guide and parent plan.
- [x] Run focused newsroom tests: 43 passed, 0 failed, 0 skipped; Release build succeeded.
- [ ] Full release verification on an environment with Docker and the required tooling.

## Validation handoff

- Personal beats are now manageable on News desk, with a bounded personal queue. The existing
  pipeline continues to produce the bell/push alerts.
- Fixed two coupled storage defects: a story awaiting corroboration no longer consumes its
  alert-history entry, and removing someone else's beat cannot erase their alert history.
  Invalid region/topic subscriptions are rejected before storage.
- Focused command (before Grant stopped testing):
  `dotnet test tests/GwsBusinessSuite.Tests/GwsBusinessSuite.Tests.csproj -c Release --filter "FullyQualifiedName~WorldNewsDeskTests|FullyQualifiedName~WorldNewsNewsroomTests" --no-restore --disable-build-servers -p:RunAOTCompilation=false -p:ValidateXcodeVersion=false -p:EnableCodeSigning=false --verbosity minimal`
  passed all 43 tests, including the new regressions.
- `scripts/verify-release.sh` was invoked but interrupted without a completed result. Grant then
  instructed that tests cannot run in this workspace now; Docker is not installed. No active
  validation process remained afterward. No Docker or other tooling was installed.
- Full-suite/browser interaction and release readiness are **unverified**, not PASS or PARTIAL.
  Resume the release gate on a supported environment before shipping.
- No commit, push, merge, publishing action, schema change or deployment was performed.

## World News continuation: shift handover and desk notes (N6/N7)

Grant narrowed the work to World News. Continue with two dependent newsroom workflows:

- [x] N6: an on-demand handover on News desk, with a selectable time window and copyable text.
  Include unchanged single-source budget stories, not only labels changed during the shift.
  Bound recent ledger reads instead of loading the lifetime hash chain.
- [x] N7: notes on each story, visible only to the admin desk. Notify only active admins named
  by a mention; never send private note excerpts to people who cannot open the desk.
- [x] Add focused regression-test sources without executing them, and update the user guide and parent plan.
- [x] Review service registration, all label-snapshot and handover callers, note access boundaries,
  story-component wiring and patch whitespace without executing application code.
- [ ] Compile and run focused newsroom/service tests, browser interaction checks and the release gate
  when testing is permitted on an equipped environment.

Constraints: no tests, release gate, Docker installation, commit, push or deployment in this
workspace. The earlier 43 passing tests do not validate this continuation.

Implementation notes:

- The handover uses the latest label for each budget story separately from recent label
  changes; it no longer reads and filters the lifetime ledger in memory. An additive
  `label_snapshots(taken_at)` index supports the time filter using the World News store's normal
  startup schema maintenance (not an EF entity migration).
- Notes are plain text and limited to 4,000 characters, with the latest 100 shown. The scoped
  desk service checks active-admin access on every read/save and only notifies active admins.
  Stories that have disappeared cannot receive orphan notes. Tied timestamps use insertion
  order so the latest notes are retained by a capped read.
- A keyed child component keeps the note editor attached to its original story while analysis
  reloads the surrounding document. Its database context comes from a fresh scope per operation.
- Regression sources cover authorization/recipients, missing stories, note validation and
  ordering, bounded ledger reads and an unchanged single-source story in a handover.
- No tests, builds, browser execution, dependency installs or release gate were run for N6/N7.
  No commit, push or deployment was performed by this assistant.

## World News continuation: private sources and approval records (N8/N9)

Grant approved proceeding, then selected **desk approvals only; leave CMS publishing unchanged**.
N9 in this unit records historical approvals, not a publishing gate or signatures over an
immutable article revision. Do not silently change CMS publishing behavior.

- [x] N8: owner-private source-book page with create/edit/remove, per-source use history,
  story/outcome recording and measured accuracy. Page through contacts and history; scope
  every query/mutation to the authenticated owner, including guessed contact ids.
- [x] N9: story approval controls showing both people, times, trust label and record hashes.
  Require a complete checklist, two verdict approvals before publication approvals, and
  distinct signers. Validate inside the existing ledger write transaction.
- [x] Add regression sources and update guides/parent plan without executing tests or builds.
- [ ] Compile, browser-check and run the release gate later in an authorized environment.

Implementation handoff:

- Contacts/history are paged at 25 entries. Accuracy uses owner-scoped aggregate counts across
  the entire history. Owner is derived from the caller, not a submitted contact's owner.
- Contact edits preserve creation time, reject invalid ratings rather than clamping, and report
  foreign-id conflicts instead of returning false success. Removal and use-history deletion
  are transactional.
- Approval prerequisites, duplicate prevention and global-chain append share one immediate
  SQLite write transaction. Reads use that same connection/transaction.
- Story children have distinct keys; the approval panel refreshes when checklist completeness
  changes and handles a change arriving during an in-flight request.
- Regression sources cover ownership, pagination/accuracy, authorization, duplicate/concurrent
  signatures, missing stories and interleaved global ledger records. They have not been run.
- No tests, builds, browser execution or release gate have run for N8/N9.

## World News continuation: public live updates (N10)

- [x] Reuse existing live-update models/store, adding active-admin posting/correction operations.
  Keep original wording visible as superseded; show confirmed/unconfirmed explicitly.
- [x] Add an isolated story editor and anonymous, read-only public index/story pages. Publish
  buttons must explicitly warn that their content becomes public; never copy private desk data.
- [x] Enforce existing holds on writes and public reads, and omit pruned/missing stories.
  Bound/paginate reads, include corrections in latest-activity ordering, and disable caching.
- [x] Add focused regression sources and update both the guide and parent plan.
- [ ] Compile, run focused/browser tests and the release gate only when Grant permits it.

This unit adds no AI calls, automatic posting, CMS approval gate, dependencies or remote Git writes.
Public pages load on opening/refresh; no background polling is planned.

Implementation handoff:

- The keyed editor uses a fresh authenticated service scope for each operation. Explicit public
  actions publish the headline and typed wording only; confirmation labels are manual.
- Live writes validate text/status and story existence and check the hold inside an immediate
  SQLite write transaction. Corrections are constrained to the requested story, preserve the
  original text, and fail explicitly for missing ids. Original plus latest correction is the
  existing storage contract, not a complete amendment audit trail.
- Anonymous static SSR pages use the bare public layout and narrow public records without
  usernames. Their SQL joins retained events, filters holds/unknown statuses, and pages results
  at 25 plus one lookahead. No private desk tables are read by public queries.
- The public index includes correction activity; individual story posts retain deterministic
  creation order. Handover now reads the latest 200 activities and includes older posts
  corrected during the selected shift, with the corrected wording and timestamp.
- Public requests set `Cache-Control: no-store`; malformed paging is 400, unavailable coverage
  is 404, and database failure is 503. Previously viewed material cannot be recalled.
- Regression sources cover validation, active-admin access, story-scoped correction, exact
  hold expiry, pagination/tied timestamps, pruned stories and corrections in handovers.
- Source/call-site and patch-whitespace review only. **No build, tests, browser execution or
  release gate run**, per Grant's restriction. No dependencies/schema changes, AI calls,
  commit, push or deployment performed by this assistant.
- Before shipping on an equipped environment: compile with zero warnings, run the focused
  newsroom/service regressions, check anonymous/held/error routes and editor interaction in a
  browser, then run `scripts/verify-release.sh`.

N11 embargo/hold management UI is paused for the English-first priority below. N12-N15 and
the other B10 reading items remain.

## Priority change: English-first collected news

Grant paused N11 before any hold-management edits and requested English for incoming and
retained news: headlines, feed descriptions and English summaries. Preserve original source
text; external publisher pages are outside the app's control.

- [x] Extend the existing translation cache/translator to keep English headlines, short feed
  descriptions and a concise report summary, tied to the source version.
- [x] Process bounded batches automatically, including ungrouped reports and retained backlog.
  Use the existing AI service/background workload scheduling, with retry delays and an
  explicit waiting/failure status. Never send private desk material to the model.
- [x] Make collected reports English-first across browsing, story coverage and exports;
  preserve originals behind an explicit Original control. Reuse English report headlines for
  grouped views while broader English story summaries catch up.
- [x] Extend existing English story summaries to retained/single-outlet events without
  changing trust labels, alert corroboration or grouping inputs.
- [x] Add regression sources and update guides. No tests/builds/browser/release gate here.
- [ ] Validate on an approved Docker-capable machine. Source implementation is complete;
  compilation, model behavior, browser layout, query timing and release readiness are unverified.

Translations and summaries are generated asynchronously, not guaranteed immediately.
Pending/failed work must be visible rather than mislabeled as translated.

### Progress / handoff

- Extended the existing `article_translations` cache rather than introducing a provider or
  parallel translation service. Added source-version checks, durable retry times and a
  separate external-content FTS index; original source text and its FTS index are unchanged.
- Added an event-to-English-report reference, populated for legacy cache entries and on
  later grouping, so world/event projections use indexed single-report lookups. A partial
  pending-article index is built off web startup by the English worker, so completed archives
  are not repeatedly scanned. Source edits requeue their report.
- The existing hosted news worker now has an independent English loop: at most eight reports
  per call, six newest/two oldest, three-minute timeout, 15-minute retry deferral, background
  AI priority and cooperative interactive cancellation. No model means quiet waiting.
- English source text appears in Reports, story coverage, story documents, grouped views,
  search-source titles and the Overwatch source panel. Originals remain explicit secondary
  content. External non-collected search previews, publisher pages and historical edit
  evidence are not translated; private/manual desk records are not sent to AI.
- A single-report English summary is labeled as such when used before a full story summary.
  Existing story summaries now consider 30 retained days and one-outlet events, still within
  their existing per-read budget. Trust/corroboration/publication rules are unchanged.
- Added parser/store/worker regression sources for identity, missing fields, legacy cache,
  retries/restart persistence, old/new fairness, FTS, stale responses, later grouping, world
  projections and public live headlines. None executed.
- Updated the ignored local user guide, including capacity/storage monitoring and rollback:
  older headline-only writers require removal of the derived English FTS objects before use.
- Source/call-site and Git patch-whitespace inspection only. No build, test, browser,
  release gate, live AI generation, commit, push or deployment performed by this assistant.
- Before release: zero-warning compile; focused World News parser/store/worker regressions;
  model output checks with several source languages and missing descriptions; a large-store
  query/catch-up measurement; original-text/pending/mobile/Overwatch browser checks; and the
  required release verification. Check upgrade and rollback on a copied database first.

## N11: hold management

Progress: source implementation written; runtime/release verification blocked.

- [x] Add active-admin hold management using the existing store and story live editor.
- [x] Validate future release times/reasons and reject missing stories; keep hold reads indexed.
- [x] Add focused regression sources and user guidance; inspect the patch without execution.
- [ ] Runtime/release verification remains blocked by the no-tests instruction.

Hold times are entered and displayed explicitly in UTC to avoid browser/server time-zone ambiguity.
Existing draft/live/alert guards remain authoritative. This does not revoke already-published
CMS articles or add a new CMS publishing gate.
The existing live editor owns the hold controls and shares its refreshed hold state, avoiding
a second store reader or stale sibling panel. Release uses inline confirmation; an absent
hold fails explicitly. Hold updates are last-writer-wins, not an immutable audit trail.
Regression sources cover desk authorization, replacement, missing stories, invalid times,
reason limits, exact expiry and live/public gating. Existing raw hold tests now seed a real
story. No build, test, browser or release-gate execution was performed. N12 is the next unit.

## N12: corrections publishing

Progress: source implementation written; runtime/release verification blocked.

- [x] Validate and authorize publication; block new notices for held/missing stories.
- [x] Add a story editor, public paged log/permalinks and methodology-log navigation.
- [x] Optionally associate a currently published CMS article and show notices beneath it.
- [x] Add focused regression sources, update the guide and inspect the patch.
- [ ] Runtime/release verification remains blocked by the no-tests instruction.

Existing notices remain public after a hold or story pruning; corrections do not silently
rewrite article text or alter CMS publication/approval rules. CMS association is explicit by
published article slug. The additive article reference lives in the existing raw SQLite
corrections table, not a new EF entity. Public projections omit the internal desk username.

The story editor requires review followed by explicit public confirmation. Existing notices
have no edit/delete/reassignment action. Public list/permalink routes use no-store responses,
deterministic paging and explicit 400/404/503 outcomes. Article notices are loaded by article
ID through WorldNewsService and rendered with encoded text, outside the article's Markdown;
25 latest notices are shown with a link to the full article-filtered log. Both publication
status and scheduling are checked when associating a CMS slug. The article body, CMS status,
trust labels, challenges and approvals are not modified.

Public blog reads now depend on the World News corrections store: on a store failure, return
503 rather than silently omit corrections. The guide documents this monitoring/rollback
concern. Regression sources cover auth, public-only association, holds, limits, stable paging,
privacy projection, retention after pruning, legacy schema upgrade and encoded HTML.
Static source/call-site and tracked patch-whitespace checks only; no build, test, browser,
release gate, commit, push or deployment performed. Next unit: N13 morgue/archive search.

## N13: newsroom morgue

Progress: implementation and focused regression sources written; release verification pending.

- [x] Add an active-admin morgue search to News desk, with bounded query/results and explicit loading/error states.
- [x] Search retained reports through the existing World News full-text and English indexes.
- [x] Include verdict/approval history and newsroom records; keep source contacts/use history owner-private.
- [x] Add focused store/service regression sources and document the workflow.
- [x] Run focused desk-store regressions (32 passed) and the morgue service regression (1 passed).
- [ ] Complete `scripts/verify-release.sh` on a supported equipped environment.

The store search also covers notes, budget entries, tips, live updates, corrections and dossiers.
Report hits link to retained stories; the existing broader World News search remains available
for the same query. Source-book contact details are not included in hits, and another person's
contacts or source history are never searched. The successful focused test runs used already-built
dependencies with analyzers disabled because the default test build reports an existing
`xUnit2031` analyzer issue elsewhere in `WorldNewsDeskTests.cs`. The full release gate was
attempted: it cannot pass here because Python and Docker are unavailable, Web has three compile
errors in untouched files, and the full suite reports unrelated Windows/browser/file-lock
failures. Browser and release readiness remain unverified. No commit, push or deployment was
performed by the assistant; Grant committed N13 as `81edda98` while work was in progress. The
branch was fast-forwarded to the refreshed `origin/main`. During N14, Grant advanced `main`
again with `6be6bd80`; after approval, the assistant fast-forwarded locally to that commit
without pushing.

## N14: story dossiers

Progress: story dossier workflow implemented and focused coverage passes; release readiness is
blocked by missing local tools and broad Windows test-suite failures.

- [x] Expose the existing dossier store through active-admin service operations with safe URL validation.
- [x] Add story-level pin/remove controls and the private dossier PDF download.
- [x] Scope removal to the requested story, make URL deduplication/cap enforcement transactional, and disable PDF caching.
- [x] Add regression sources and document the workflow.
- [x] Run focused checks: `WorldNewsDeskTests` 33/33 and the dossier service/PDF test 1/1.
- [ ] Get a green `scripts/verify-release.sh` and verify the interactive browser flow.

Pins are shared desk records. PDF exports include up to 100 private desk notes, checks, sign-offs
and up to 40 report sources; they do not include personal source-book contacts. Snapshot/image
files are represented by HTTP/HTTPS links, not embedded uploads. No commit, push or deployment
was performed by the assistant.

The required release gate was run on 17-11-26 against `6be6bd80`. Restore, dependency audit and release build passed;
responsive lint could not run because Python is unavailable, Docker Compose rendering could not
run because Docker is unavailable, and the full suite failed (318 failures, 4139 passed), including
Windows SQLite file-lock cleanup failures. Running the service test class as a group also hit
Windows SQLite cleanup locks; the N14 service/PDF regression passed alone. No browser workflow was
independently verified.

## N15: desk metrics

Progress: implementation and local validation complete; browser/deployed acceptance remains.

- [x] Expose the existing desk calculations through the active-admin service boundary.
- [x] Add all-time desk KPIs and open items per person to News desk with loading, refresh and error states.
- [x] Correct even-sample median calculation and add focused authorization/calculation regressions.
- [x] Run focused tests and `scripts/verify-release.sh`.
- [ ] Verify the interactive desk layout and refresh behavior in a browser.

Metrics use retained desk records: budget ownership/status history, two-person verdict sign-offs,
current trust labels, tips linked to stories, live updates and public corrections. “Held up” means
the current label is not disputed and is at least as strong as the label at verdict. The dashboard
does not rank people or infer individual performance from shared outcomes. N15 is the final newsroom
unit; B10 reading work remains separate.

Focused desk/service coverage passed 51/51. The first release-gate run exposed an N14 scoped-CSS
regression: the dossier used a heading class styled only by its parent component. The dossier now
owns that style; the CSS guard plus focused coverage passed 53/53. The final release gate was
`PARTIAL`: responsive lint, whitespace, Docker rendering, restore, dependency audit, zero-warning
Release build and all 4,459 automated tests passed. Responsive/deployed checks were not run because
no targets were supplied; the Mac build was correctly skipped because this unit does not touch it.
