# Sentinel shared continuation handoff (Codex + Claude)

Updated 2026-10-09. The approved master scope is
`.claude/plans/sentinel-best-in-class-2026-10.md`; this file is the execution handoff so Codex or
Claude can resume without rediscovery or overlapping completed work.

## Ownership and safety

- Grant alone commits, pushes, merges, publishes and deploys unless explicitly authorized.
- Inspect both `git diff --cached` and `git diff` before edits. Preserve unrelated user changes.
- Never dispatch a deployment merely to validate local work.
- For every fix: update its `docs/*_USER_GUIDE.md`, run build, the full test suite, and
  `./scripts/verify-release.sh`; report responsive/deployed/physical-device checks as NOT RUN
  unless actual target evidence exists.

## Baseline and current worktree

- `origin/main`: `880bce38`. Item 7 is committed locally as `64d98120` (git author Grant
  Watson; Claude ran no commit); local `main` is one commit ahead until Grant pushes it.
- The item-7 implementation is committed locally: local-Ollama database design,
  strict proposal parsing, read-only preview, explicit Confirm-and-create, persistence service,
  and tests. The master tracker marks item 7 complete and the local Sentinel guide is updated.
- Item-7 evidence before the later CI repairs: web build passed with zero warnings/errors;
  137 focused related tests passed; full assembly passed 2,769 tests; release verification passed
  every supplied local gate and was PARTIAL only because responsive/deployed targets were absent.
  Report: `artifacts/release-readiness/release-20261009T220027Z.md`.

## GitHub failures from pushed commit 880bce38

1. Deploy run `37997413915` stopped before deployment. Ubuntu's ambient locale formatted an
   explicitly-USD invoice total as `¤2,500.00`; `ContactTimelineServiceTests` expected `$2,500.00`.
2. Native run `37997413912`: Android and Windows passed. Apple installed .NET Apple workload
   `27.0.10722` but forced Xcode 26.6; the workload requires Xcode 27.0.

Local repairs now present:

- `ContactTimelineService` uses `en-US` explicitly for USD deal/invoice values.
- `.github/workflows/clients.yml` runs the Apple job on `runs-on: xcode-27` and selects
  `/Applications/Xcode_27.0.app/Contents/Developer`. Selecting the path alone would still fail:
  `macos-latest` (macos-26-arm64, image 20260907, per actions/runner-images) tops out at Xcode
  26.6. The xcode-27 image is a GitHub public preview; if it proves flaky, the fallback is pinning
  the MAUI workload to a version that supports Xcode 26.6 on macos-latest.
- `VoiceNotesPage` Mac-only recording fields moved inside `#if MACCATALYST` (the solution build
  had 4 android/ios warnings; now 0 warnings, 0 errors).
- Claude ran checklist steps 1-6 on 2026-10-09: all local gates pass; results are in the master
  plan progress log (report `artifacts/release-readiness/release-20261010T002054Z.md`).
- `docs/CRM_USER_GUIDE.md` documents locale-independent USD display.

Do not call CI fixed until Grant commits/pushes and replacement runs pass.

## Immediate validation checklist

1. Run `ContactTimelineServiceTests`; also force a neutral culture if practical.
2. Verify workflow YAML and that the observed `macos-latest` image exposes Xcode 27.0.
3. Run `dotnet build GwsBusinessSuite.slnx`; distinguish product errors from a local
   Xcode/workload mismatch.
4. Run the full test assembly, not only filters.
5. Run `./scripts/verify-release.sh`; restore, vulnerability audit, Release build, full suite,
   Compose rendering, breakpoint lint and whitespace must pass.
6. Add final counts/results to the master plan progress log.
7. Stop for Grant to commit/push. Then monitor both replacement workflows and inspect logs.

## Next implementation: item 13, date mentions and reminders

Goal: `@today`, `@tomorrow`, and phrases such as `@next Friday 3pm` insert a date chip. A user can
set a reminder on it; a minute sweep sends exactly one collaboration-bell notification linking to
the originating page and block.

### Discovery

- Read `wiki-block-editor.js` mention-menu, rich-text serialization and .NET interop paths.
- Read `WikiBlockJson`, `WikiRichTextSpan`, HTML/export rendering, collaboration notification
  service/entity, and existing hosted sweep implementations.
- Use Unix-second columns for due/sent queries; SQLite cannot reliably order `DateTimeOffset`.

### Domain and persistence

- Add a deterministic date-expression parser accepting a `TimeProvider` and explicit time zone.
- Minimum grammar: today, tomorrow, next weekday, and optional 12/24-hour time. Reject ambiguity.
- Persist an absolute UTC instant and original label in backward-compatible rich-text metadata;
  compute relative display text at render time.
- Add `PageReminder`: page id, block id, owner username, due/sent/created Unix seconds and
  cancellation state. Index due-unsent rows and page/block lookup. Add one intentional migration.

### Service and authorization

- Create/list/cancel only for an authenticated user who can view the page. Never trust a client-
  supplied reminder owner; use the current identity.
- A bounded minute sweep claims due reminders idempotently, emits one existing bell notification,
  includes page/block navigation, and durably stamps sent state.
- Repeated sweeps, restarts, cancellation, deleted pages and access changes must not duplicate or
  leak notifications.

### Editor and Blazor UI

- Add date candidates to the existing `@` menu without regressing people mentions.
- Render presentation-only relative chips (`today`, `tomorrow`, `in 2 days`); the relative phrase
  is never the persisted source of truth.
- Chip action opens reminder choices and supports cancellation/current-state display.
- No inline script (CSP). JS-created chip styles belong in global `app.css`, not scoped CSS.

### Required tests

- Parser: time zones, DST, boundary dates, malformed/ambiguous phrases, deterministic clock.
- Service: auth, early/due/cancelled/deleted-page cases and exactly-once repeated sweeps.
- Renderer/export: date metadata and safe fallback for old/unknown data.
- Playwright: type/select `@today`, serialize/reload the chip, invoke reminder interop, and prove
  menu hints/placeholders never become saved content.
- Update `docs/SENTINEL_USER_GUIDE.md`, master item 13 and its progress log, then run full gates.

## Order after item 13

Continue Phase 1 in master order: 14 hover previews, 15 aliases, 17 diagrams, 19 focus/reading
stats, 20 PDF/Word export; then Phases 2 and 3. Each Phase-4 item (9, 10, 11, 12) gets its own
reviewed sub-plan before code. Stop for Grant's choice before item 29 (mailbox/provider), item 8
(unpacked extension versus store), and item 9 (timing/risk).

## Non-negotiable invariants

- Local AI only; no external LLM APIs.
- Governed model writes always show a preview and require explicit confirmation.
- Authorization belongs in services/endpoints, not only hidden UI.
- Editor hints/placeholders never serialize as user content.
- Page creation saves the parent link and opens the real child page.
- No production, responsive, physical-device or public-route acceptance claims without evidence.
