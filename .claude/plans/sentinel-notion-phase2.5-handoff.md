# Handoff: Sentinel → Notion parity, Phase 2.5 in progress

## Context
Repo: `/Users/grantwatson/Desktop/Development/CSharp/GwsBusinessSuite` (Blazor Server, auto-deploys
working-tree changes to `origin/main` on every edit — treat all changes as already shipped, per
CLAUDE.md). Ongoing initiative: make "Sentinel" (the internal Notion-style wiki/database tool,
6 users total, no external access) match Notion's feature set as closely as reasonable.

Standing instruction from the user: keep working through the phases without stopping to ask
between them. Session was stopped mid-Phase-2.5 due to hitting a session usage limit — this is a
genuine stop, not a decision point; just continue the plan below.

## Verified-safe state as of hand-off
- `dotnet build GwsBusinessSuite.slnx` — succeeds, 0 errors, 0 warnings.
- Last full test run: 946/946 passing (`dotnet test tests/GwsBusinessSuite.Tests/GwsBusinessSuite.Tests.csproj`).
- Last `./scripts/verify-release.sh` run: PASS on every local check (Restore, vulnerability audit,
  Release build, Full automated suite, Docker Compose rendering, Patch whitespace). "Overall: PARTIAL"
  only reflects the "Deployed *" checks that need a live URL — not a real failure.
- Working tree has real, uncommitted (this repo has no manual commit step — some external process
  auto-publishes) changes for everything below. Nothing is currently mid-edit in a broken state —
  build is green — but **one item (Sub-items) is only half-done**, see below.

## Completed this session (Phases 2.1–2.5 so far)

**Phase 2.1** — 7 new database property types: Status (grouped Select), LastEditedTime,
LastEditedBy, CreatedBy, Email, Phone, Button. Backend (Domain/Application/Infrastructure) + full
UI wiring in `WikiDatabaseEditor.razor` (type dropdown, Status option/group editor, Button
workflow picker, per-cell editors/renderers). Migration `20260808165256_AddWikiTrashSupport` is
unrelated/earlier; property types needed no schema change (stored in existing `ConfigJson`/
`PropertyValuesJson` text columns).

**Phase 2.2** — 6 new rollup aggregations: countEmpty, countNotEmpty, percentEmpty,
percentNotEmpty, median, range. `WikiDatabaseViewLogic`/`WikiDatabaseComputation.EvaluateRollup`.

**Phase 2.3** — 9 new formula array/list functions: first, last, at, slice, unique, join,
includes, sort, plus polymorphic `length`. Deliberately did NOT implement `map`/`filter` (need
lambda/predicate syntax — real parser-design gap, not yet raised with the user beyond this note).

**Phase 2.4** — Nested AND/OR filter groups (`WikiDatabaseFilterGroup`, new optional field on
`WikiDatabaseViewConfig`, backward-compatible with the old flat `Filters` list) +
`WikiDatabaseFilterGroupEditor.razor` recursive UI component. **Real bug found and fixed during
this work**: `results.Any()` with no predicate on an `IEnumerable<bool>` checks "is the sequence
non-empty", not "is any element true" — every OR-group condition was matching every row until
fixed to `results.Any(matched => matched)` (`WikiDatabaseViewLogic.MatchesGroup`).

Also Phase 2.4 — Personal per-user view overrides: new entity `WikiDatabaseViewPersonalization`
(migration `20260808220805_AddWikiDatabaseViewPersonalization`, already applied/generated), new
`IWikiDatabaseService.GetPersonalViewOverrideAsync`/`SavePersonalViewOverrideAsync`/
`ClearPersonalViewOverrideAsync`, UI toggle in `WikiDatabaseEditor.razor`'s View options panel
("Personalize filters & sort"). Only Filters/Sorts/FilterGroup are ever personalized — grouping,
page property order, etc. always stay shared.

**Phase 2 verification** — full suite + verify-release.sh, both clean, at this checkpoint.

**Reconciliation against a fresh Notion feature inventory the user pasted mid-session** — audited
via a research subagent (Explore) against the actual codebase, not guesswork. Confirmed genuinely
missing (→ became Phase 2.5, see below): `unique_id` and `verification` property types, sub-items
(row parent/child hierarchy within one database), linked database views (a filtered live subset of
another database), per-database row templates (distinct from `SentinelDatabaseTemplate`, which
only clones a whole database's schema), standalone CSV import (today only reachable inside the
Notion-workspace-export import flow), Timeline row dependencies, and a Tab block (Columns block
already exists). Everything else in that inventory is either already present or already tracked
in Phase 3–5. Multi-product/Enterprise items (Notion Mail/Calendar/Sites/Web Clipper, Admin API,
SSO/SCIM, legal holds, SIEM, public developer API/Workers/webhooks) are being treated as
deliberately out of scope — Sentinel is one internal tool for 6 people, not a public platform.

**Phase 2.5, done:**
- `unique_id` property type: `WikiDatabasePropertyTypes.UniqueId`, `WikiDatabasePropertyConfiguration.UniqueIdPrefix`,
  auto-assigned exactly once at row creation (`WikiDatabaseService.SaveRowAsync`, the `isNew`
  branch — next id = max already used by any sibling row incl. trashed ones, +1, never reused),
  read-only afterward (rejected in `SaveInlineCellAsync`'s computed-property guard). Full UI:
  add-property dropdown, prefix config input, read-only table cell. **Real bug found and fixed
  during this work**: the already-assigned UniqueId was being silently wiped on every subsequent
  edit of the row, because `computedPropertyIds` exclusion strips the key from `editor.Values`
  every save, and `editor.Values` is the *complete* replacement set — fixed by adding an `else`
  branch (row is not new) in `SaveRowAsync` that carries the previous stored value forward for
  every id in `computedPropertyIds` (a no-op for Formula/Rollup/Button/LastEdited*/CreatedBy since
  those were never actually stored in `PropertyValuesJson` to begin with — only UniqueId needed it).
- `verification` property type: `WikiDatabasePropertyTypes.Verification`, `WikiVerificationState`
  record (Status/VerifiedBy/VerifiedAt, JSON-serialized into the row's scalar storage slot via
  `WikiPropertyValues.SetVerification`/`GetVerification`), toggled via `SaveInlineCellAsync`
  (value `"verified"`/`"none"`, server stamps `performedBy`+`now`) and via a dedicated
  `SetVerificationAsync` in `WikiDatabaseEditor.razor` (a `<button>` cell, not the generic
  value-string path, since it needs the current user stamped). Full UI: dropdown option, table
  cell toggle button (✓ Verified / Not verified).
- Both have new tests in `WikiDatabaseServiceTests.cs`:
  `SaveRowAsync_ShouldAutoAssignSequentialUniqueIdsAndNeverReuseThem`,
  `SaveInlineCellAsync_ShouldToggleVerificationAndStampWhoVerifiedIt`, plus UniqueId added to the
  existing `SaveInlineCellAsync_ShouldRejectComputedPropertyTypes` theory. 946/946 passing after
  this, confirmed via a full suite run.
- No new EF migration needed for either — both stored in existing `ConfigJson`/`PropertyValuesJson`
  text columns.

**Phase 2.5, half-done (STOPPED HERE — resume this first):**
- Sub-items (row parent/child hierarchy within one database). So far: added
  `public Guid? ParentRowId { get; set; }` to `WikiDatabaseRow` in
  `src/GwsBusinessSuite.Domain/Entities/CoreEntities.cs` (with an explanatory comment already in
  place — self-referencing, distinct from Relation, intended as SET NULL on parent delete). Build
  still succeeds (this alone doesn't require a migration to compile, only to actually persist/read
  correctly against a real SQLite file — **a fresh in-memory test DB via `EnsureCreatedAsync()`
  will pick the new column up automatically since it builds the schema from the current model, but
  the real `wiki.db` file on disk will NOT have the column until a migration is generated and
  applied** — do not skip the migration step).
- Was about to look at `Comments.ParentCommentId`'s self-referencing FK config in
  `src/GwsBusinessSuite.Infrastructure/Data/ApplicationDbContext.cs` (around line 204 and 313 —
  there are two `modelBuilder.Entity<Comment>()...HasForeignKey(x => x.ParentCommentId)` blocks,
  likely once for the FK itself and once for something else, worth reading both fully) as the
  precedent to copy: `ON DELETE SET NULL`, so a deleted parent row automatically un-parents its
  children rather than needing manual reparent-before-delete logic in the service layer. **Do this
  the same way for `WikiDatabaseRow.ParentRowId`** — no navigation property was added on
  `WikiDatabaseRow` for the parent, so this will need an explicit
  `modelBuilder.Entity<WikiDatabaseRow>().HasOne<WikiDatabaseRow>().WithMany().HasForeignKey(x => x.ParentRowId).OnDelete(DeleteBehavior.SetNull)`
  (or add a `WikiDatabaseRow? ParentRow` nav property first, whichever reads cleaner given how the
  rest of the file is structured — check a few more self-referencing examples before deciding).

## Remaining Phase 2.5 work (not started)

1. **Finish Sub-items**:
   - `IAppDbContext`/`ApplicationDbContext`: no new DbSet needed (still `WikiDatabaseRow`), just
     the FK config above.
   - Migration: `dotnet ef migrations add AddWikiDatabaseRowParentRowId --project src/GwsBusinessSuite.Infrastructure --startup-project src/GwsBusinessSuite.Web`
     — review the generated file before moving on (same pattern used successfully earlier this
     session for `AddWikiDatabaseViewPersonalization`).
   - `WikiDatabasePropertyModels`/`WikiDatabaseRowEditor`: add `Guid? ParentRowId` (treat as a
     normal settable field, not null-preserves like `BlocksJson`/`Icon` — reparenting should be an
     explicit, deliberate action, so a null value should mean "make this a root row", not "don't
     touch the parent").
   - `WikiDatabaseService.SaveRowAsync`: accept and persist `editor.ParentRowId` onto the row
     entity (a plain column, not part of `PropertyValuesJson`). Validate: parent (if set) must be
     in the same `wikiDatabaseId`, and must not be the row itself or one of its own descendants
     (walk up from the proposed parent via `ParentRowId` chain, throw `InvalidOperationException`
     on a cycle — mirror the existing self-relation cycle-guard style already used elsewhere in
     this file for Relation/reciprocal setup, if any exists; otherwise write a straightforward
     loop-with-visited-set).
   - `DeleteRowPermanentlyAsync`: confirm `SetNull` FK behavior is sufficient (it should be — no
     manual cleanup code needed) but double check trashing (`TrashRowAsync`) — decide and document
     whether trashing a parent should also hide its children from normal views (simplest,
     consistent-with-today choice: **don't** cascade trash to children, same as this app already
     doesn't cascade trash through Relations — document that choice in a comment either way).
   - UI (`WikiDatabaseEditor.razor` table view, likely also `WikiDatabaseFilterGroupEditor.razor`'s
     sibling components, and `SentinelDatabaseRowPage.razor` if it shows a "sub-items" section):
     indent+group child rows under their parent, an expand/collapse toggle per parent row, a
     "+ Sub-item" action per row (creates a new row with `ParentRowId` = that row's id). Given the
     rest of this session's UI work stayed functional-not-fancy, match that bar — a working nested
     list is enough, no drag-to-reparent needed for v1.
   - Tests: creation/reparenting, cycle rejection, cross-database rejection, delete-parent
     un-parents children (SetNull), at minimum.

2. **Linked database views** (filtered live subset of another database, shown without duplicating
   data). Today `WikiDatabaseView` always belongs to exactly one `WikiDatabaseId`, and the
   `InlineDatabase` block embeds a source database's *entire* content with no filter. Needs design
   thought before coding: likely a new view "mode" or a variant of `InlineDatabase` that carries
   `SourceDatabaseId` + a `WikiDatabaseFilterGroup`/`Filters` list, rendered read-only (or
   editable-through-to-source, decide and document which) against the source database's live rows.
   Not started at all — no entity/model changes made yet.

3. **Per-database row templates** (preset starting content for a *new row* inside one specific
   database — pick from one or more templates when clicking "New row", distinct from
   `SentinelDatabaseTemplate` which clones a whole database's schema+views to create a *new
   database*). Not started. Likely needs a new small entity (e.g.
   `WikiDatabaseRowTemplate { WikiDatabaseId, Name, BlocksJson, DefaultPropertyValuesJson }`) +
   migration + a picker in the "Add row" UI when more than one template exists for that database.

4. **Standalone CSV import** into any existing database (as opposed to CSV import that only
   happens inside a full Notion-workspace-export ZIP today, in
   `SentinelWorkspaceImportService.ParseCsv`/`ReconcileDatabase`). Not started. Reuse
   `SentinelWorkspaceImportService.ParseCsv`'s header→property and row→row logic if it's cleanly
   extractable, exposed as a new `WikiDatabaseService` method taking a raw CSV string + target
   `wikiDatabaseId`, plus a small upload UI in `WikiDatabaseEditor.razor`.

5. **Timeline row dependencies** (blocking/depends-on links between rows, Gantt-style). Today
   `WikiDatabaseModels.BuildTimeline` groups purely by a single Date property — no dependency
   concept anywhere. Not started. Likely needs a `DependsOnRowIds` concept (could reuse the
   Relation property machinery pointed at the same database, or a dedicated field — decide which
   fits better) plus a rendering change in the Timeline view UI to draw the dependency lines.

6. **Tab block** (tabbed container, like the existing `Columns` block but with only one visible
   tab at a time + a tab switcher). Not started. `WikiBlockTypes` lives in
   `src/GwsBusinessSuite.Application/Wiki/WikiBlockModels.cs` (see `Columns` at line 33 for the
   closest existing pattern to copy — it stores per-column content in `Props`, rendered by
   `WikiBlockHtmlRenderer.RenderColumns` at `WikiBlockHtmlRenderer.cs:263`). A `Tab` block needs
   its own render method + editor UI (likely in whatever component renders/edits `Columns` today —
   find it via the same file before starting).

7. **Phase 2.5 verification**: once all of the above lands, run
   `dotnet build GwsBusinessSuite.slnx`, the full test suite, and `./scripts/verify-release.sh`
   before moving on — same checkpoint discipline used after every phase this session.

## After Phase 2.5: Phases 3–5 (not started, unchanged from the original roadmap)

- **Phase 3**: Automation conditional/recurring triggers + new actions (add row, edit rows
  elsewhere, notify); wire the Button property to actually fire an automation from the UI (today
  it renders as a disabled button with a "not wired up yet" tooltip — see
  `WikiDatabaseEditor.razor`'s `WikiDatabasePropertyTypes.Button` table-cell case); a real
  tool-calling loop for SentinelGPT; verify/finish the `DatabaseAutofill` row-write path.
- **Phase 4**: real synced-block propagation; KaTeX equations + vendored code syntax highlighting
  (no CDN); nested toggle content; version history beyond the 20-revision cap; Markdown/CSV export
  out of Sentinel.
- **Phase 5**: Chart line/donut types; Timeline drag-resize; a real Map view; command palette
  (Ctrl+K); starter templates; comment filtering; search author/date filters + comments/AI-history
  in search scope; split the generic `embed` block into distinct video/audio/file/PDF block types.
- Full `verify-release.sh` again at the very end of Phase 5.

## Working conventions established this session (follow these)

- Every new feature got: build → targeted test file run → (if touched broadly) full suite run →
  (at phase boundaries) `./scripts/verify-release.sh`, in that order, before considering it done.
- New tests go in the existing test file matching the area (`WikiDatabaseServiceTests.cs` for
  service-layer behavior, `WikiDatabaseViewLogicTests.cs` for pure filter/sort/view logic) —
  don't create new test files for small additions.
- EF migrations: `dotnet ef migrations add <Name> --project src/GwsBusinessSuite.Infrastructure --startup-project src/GwsBusinessSuite.Web`,
  then actually read the generated `.cs` file before moving on.
- UI additions stayed functional/plain (Bootstrap classes already in use throughout the file, no
  new design system) — match that, this app's admin UI is not a polish target right now.
- When a new computed/system-managed property type is added, remember to update *all* of:
  `SaveInlineCellAsync`'s read-only guard, `SaveRowAsync`'s `computedPropertyIds` exclusion,
  `GetDisplayText`, `BuildInlineSnapshot`'s `IsReadOnly` flag + `GetInlineValue`, the formula
  engine's `ReadStoredValue` in `WikiDatabaseComputation.cs`, `AddablePropertyTypes` in
  `WikiDatabaseEditor.razor`, and the property-editor + add-property UI panels — it's easy to miss
  one of these (the UniqueId-wiped-on-edit bug this session came from missing the "carry forward
  on edit" case).
- This repo auto-publishes on every edit — there is no separate "ready to ship" gate. Keep the
  build/tests green at every stopping point, not just at the very end.
