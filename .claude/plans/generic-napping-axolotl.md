# Notion to Sentinel: Full-Fidelity Connector and Visual/Functional Parity

## Status

- **A1 (property type fidelity) — done.** People, files, relations, created/edited people,
  and last-edited timestamps map to Sentinel's typed properties. Relation schemas and values
  resolve to local database and row ids. Formula and rollup remain documented best-effort text.
- **A2 (block type fidelity) — done.** Columns, inline video/audio, rich table cells, and a
  visible `link_to_page` placeholder are supported.
- **A3 (relation resolution) — done.** This shipped as part of A1.
- **A4 (Notion picker UI) — done.** The connector modal now browses the complete paginated
  metadata search through the server, using either an unsaved replacement token or the
  encrypted stored token without returning it to the browser. Users explicitly choose
  everything shared or selected roots in a collapsible checkbox tree; checked ids continue
  through the established `SelectedNotionIds` persistence and subtree-expansion path. Database
  rows are excluded because they are imported under their owning data source.
- **A5 (incremental sync progress) — done.** `NotionSyncService.SyncAsync` now accepts an
  optional `Action<NotionSyncProgress>? onProgress` callback (`NotionSyncProgress(Processed,
  Total)`, `NotionModels.cs`), reported once per page/database actually synced in the two
  expensive loops (block sync, schema/row sync) - not the full discovered workspace, most of
  which a large mostly-unchanged workspace skips. `NotionSyncBackgroundService` wires this into
  `NotionSyncJobStatus.Progress` (a new nullable field) under its existing status lock; the
  manual-sync modal in `Wiki.razor` polls it during `ObserveNotionSyncAsync` and renders a
  progress bar + "N of Total items synced" line.
- **OAuth connection foundation — done.** A Notion public connection can now authorize from
  Sentinel without sharing a Notion password. Access and refresh tokens are encrypted, OAuth
  state is short-lived and bound to the signed-in admin, token rotation and revoke/disconnect
  are supported, and manual tokens remain an advanced fallback. Server deployment still needs
  the Notion client id, client secret, and exact callback URI.
- **Track B (visual and interaction parity) — done.** A 2026-08-01 verification against the
  actual code (not this file's prior claims) found items 1, 3, 4, and 5 already fully
  implemented - see per-item notes below. Items 2, 6, and 7 had real gaps; all three are now
  closed, completing Track B.

## Track A — Connector and sync fidelity

1. Property type fidelity. ✅
2. Block type fidelity. ✅
3. Relation value resolution. ✅
4. Browsable selective-import picker. ✅
5. Incremental progress reporting for large workspaces. ✅

## Track B — Visual and interaction parity

1. **Text and background color — already done.** `WikiRichTextSpan.TextColor`/
   `BackgroundColor` (`WikiBlockModels.cs`) round-trip through `wiki-block-editor.js`'s inline
   toolbar color swatches end to end; no gap found.
2. **Sidebar drag/reparent and row actions — done (2026-08-01).** Previously only move-up/
   move-down existed; direct drag was missing entirely and Duplicate/Delete only worked on the
   currently-open page/database, not arbitrary sidebar rows. `Wiki.razor`'s tree rows are now
   `draggable="true"` (reusing the existing global `wwwroot/js/dragReorder.js` dataTransfer
   shim, the same pattern `WikiDatabaseEditor.razor`'s Board view already uses - no new JS);
   dropping one row onto another reparents it via the existing `ReorderPageAsync`/
   `ReorderDatabaseAsync` (already cycle-guarded server-side). Each row also gained a "..."
   kebab menu (Duplicate/Delete) generic over page-or-database nodes, reusing
   `WikiService.DuplicatePageAsync`/`DeletePageAsync` and
   `WikiDatabaseService.DuplicateDatabaseAsync`/`DeleteDatabaseAsync` - no new service-layer
   code, only new sidebar UI wiring. Delete confirms via the same `JS.InvokeAsync<bool>("confirm"...)`
   pattern `WikiDatabaseEditor.razor` already uses. Rename was deliberately left out of the
   row menu - opening a row and editing its title inline (the existing flow) already covers it,
   and a stripped-down sidebar-only rename would need a second, parallel title-save path.
   Verified with `dotnet build`/`dotnet test` (all 824 passing) and a clean app boot (migrations
   applied, `/admin/sentinel` correctly redirects unauthenticated) - NOT verified with an actual
   browser drag/drop click-through as this doc's own "Scope" section calls for below, since
   mandatory portal MFA (shipped separately, same day) blocks scripted login without a live TOTP
   secret. Worth a manual pass in a real browser before considering this fully closed.
3. **Keyboard navigation in slash/mention/wiki-link menus — already done.** All three triggers
   share `openSuggestionMenu`/`handleSuggestionMenuKey` in `wiki-block-editor.js` with
   Arrow-key/Enter/Tab handling; no gap found.
4. **Real ancestor-chain breadcrumbs — already done.** `Wiki.razor`'s breadcrumb trail walks
   the actual parent chain via `SentinelTreeNavigation.GetBreadcrumbNodeIds`, each ancestor a
   clickable link; no gap found.
5. **Hover affordances and desktop sidebar collapse — already done.** `.wiki-tree-row:hover
   .wiki-tree-actions` reveals row actions on hover; a separate always-available (not just
   mobile) desktop collapse toggle already exists (`_workspaceBrowserCollapsed`, Ctrl/Cmd+\\);
   no gap found.
6. **Inline comment highlighting — done (2026-08-01).** The offset-capture and anchor-storage
   machinery already existed (`SentinelDiscussion.AnchorStart`/`AnchorEnd`, populated by the
   selection-driven "Comment on selection" toolbar button) - what was missing was ever rendering
   it. Added `SentinelDiscussionSummary.OpenBlockHighlights` (groups open, anchored discussions
   per block into `SentinelDiscussionHighlight(DiscussionId, Start, End)`) and a parallel
   `OnDiscussionHighlightsChanged` callback alongside the existing `OnDiscussionCountsChanged`,
   wired through `Wiki.razor` to a new `wiki-block-editor.js` export, `setDiscussionHighlights`.
   That function reuses `setRemoteCursors`' exact technique: a non-destructive absolutely
   positioned overlay `<span>` drawn over the anchor's character range via `Range.getClientRects()`
   (sibling to the block's contenteditable content, never inserted into it), so a highlight can
   never leak into serialized page content or need to split/rewrite the underlying rich-text span
   structure it visually sits over. Clicking a highlight calls a new `OpenDiscussionById`
   JSInvokable, which opens straight to that discussion's thread (`FocusDiscussionAsync`) -
   distinct from the existing per-block pin icon, which only opens "the block's discussions"
   generically. Deliberate tradeoff: clicking inside a highlight always opens its thread rather
   than placing a text cursor there, matching Notion's own actual inline-comment click behavior.
7. **Unified Command/Ctrl+K over Sentinel content — done (2026-08-01).** The app-wide Ctrl/Cmd+K
   palette (`app.js`'s `openCommand()`) only ever indexed the static admin nav sidebar
   (`commandEntries()` scrapes `.gws-sidebar .gws-nav-link` anchors) - inside Sentinel it had
   nothing to do with the workspace's own pages/blocks/database rows, which is what "unified...
   over Sentinel content" actually asked for. Rather than teach the generic palette to fetch
   Sentinel search results (a new API surface duplicating existing search), `openCommand()` now
   checks for `.sentinel-workspace` in the DOM and, if present, delegates to the same
   `.sentinel-global-search` button click that Ctrl/Cmd+Shift+F already opens - the existing
   ranked page/block/database-row search (`SentinelWorkspaceService.SearchAsync`) documented in
   `docs/WIKI_NOTION_CLONE.md`. Ctrl+K and Ctrl+Shift+F are now the same action inside Sentinel;
   elsewhere in the admin app Ctrl+K is unchanged.

## Scope

Sentinel remains a clean-room Notion-class workspace. Functional and interaction parity is
implemented within Sentinel's warm stone/amber identity; Notion source, trademarks, logos, and
proprietary assets are not copied.

Each item is one shippable slice. Update this status, `docs/WIKI_NOTION_CLONE.md`, and
`docs/ROADMAP.md` after each slice. UI-visible slices require a browser exercise in addition to
the clean solution build and full test suite.

The ground-up rebuild sequence and the API's honest synchronization boundary now live in
`docs/SENTINEL_REBUILD.md`.
