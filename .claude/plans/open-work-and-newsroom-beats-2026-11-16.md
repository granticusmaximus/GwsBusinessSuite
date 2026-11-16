# Open work review and newsroom beats (16-11-26)

Grant requested a review of open plans and implementation of remaining work. Started from a
clean checkout at `d691dc6e`, with `HEAD..origin/main` equal to zero after fetching.

## Review

The shared tracker contains 65 plans and historical handoffs. Unchecked boxes alone are not a
reliable backlog: later progress entries and current code supersede several of them.

| Area | Remaining work / disposition |
| --- | --- |
| World News trust and newsroom | N5-N7 UI is now implemented (N6/N7 unvalidated). N8-N15 still need UI/endpoints. B10 reading features remain. Extend the existing backend, not a second desk/store. |
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
