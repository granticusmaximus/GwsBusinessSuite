# Sentinel: Notion-class connected workspace

This document tracks broader capability parity and intentional limitations. The bounded
Sentinel v1 feature score lives in `SENTINEL_REBUILD.md`; current whole-suite production
readiness and required evidence live in `RELEASE_READINESS.md`.

Sentinel is the GWS Business Suite's clean-room, capability-level clone of Notion: a connected
workspace of nested pages, blocks, databases, collaboration, search, templates, permissions,
and AI-assisted knowledge work. Internal CLR/database names retain the established `Wiki*`
prefix to avoid a destructive rename migration, while the product, routes, navigation, and
documentation use Sentinel. It does not copy Notion source code, UI assets, trademarks, or
proprietary schemas; public Notion product and API documentation is behavioral research only.

## Architecture

- `WikiPage` stores page metadata (`Title`, `Slug`, `Icon`, `CoverImageUrl`,
  `ParentWikiPageId`, `SortOrder`) plus `BlocksJson` — an ordered, flat list of
  `WikiBlock` records (see `IReadOnlyList<WikiBlock>` in
  `src/GwsBusinessSuite.Application/Wiki/WikiBlockModels.cs`), not a nested block tree.
  Nesting (indented list items, toggle contents) is modeled as an `IndentLevel` on each
  block rather than parent/child block references — this keeps drag-reorder and
  indent/outdent editor logic to flat-array operations instead of a recursive tree,
  at the cost of not producing perfectly semantic nested `<ul>`/`<ol>`/`<details>` HTML
  (the read-only renderer emits visually-indented sibling elements instead — see
  `WikiBlockHtmlRenderer`'s doc comment for the specific trade-off).
- `WikiRichTextSpan` (`Text`, `Bold`, `Italic`, `Strikethrough`, `Code`, `Link`) is the
  inline-formatting unit inside a block, deliberately shaped like Notion's own `rich_text`
  annotation arrays rather than raw HTML or Markdown — this sidesteps the HTML↔Markdown
  lossy-round-trip problem the CMS Builder explicitly avoided for the same reason (see
  `CmsBlockHtmlRenderer.cs`'s comment on why widget Markdown props stay non-contenteditable),
  and means a future Notion API import maps close to directly onto this model.
- Page history is bounded DB snapshots (`WikiPageRevision`, 20 kept per page, oldest
  trimmed on save), the same pattern `CmsPageRevision`/`PageRevisionService` already use for
  the CMS Builder — not git commits. The wiki was previously backed by a real local git
  repository (LibGit2Sharp, one commit per save, history read live from git log); that
  layer has been removed.
- The block editor (`wwwroot/js/wiki-block-editor.js`) owns the DOM while a page is being
  edited — it's an ES module following `automation-editor.js`'s interop shape
  (`initialize`/`dispose`, `DotNetObjectReference`, Pointer Events for drag), not the CMS
  Builder's iframe/postMessage bridge (that pattern exists specifically because the CMS
  canvas previews the live public-render route in an iframe; the Sentinel editor has no such
  constraint). Blazor receives a serialized snapshot via a single `OnBlocksChanged`
  callback (mirroring the old `OnMarkdownChanged` shape) and persists it on explicit Save.
- `IWikiService`/`WikiService` (`src/GwsBusinessSuite.Infrastructure/Services/WikiService.cs`)
  own page CRUD, version history, structural diffing, and page reordering/reparenting
  (`ReorderPageAsync` — cycle-guarded, renumbers siblings). Content saves
  (`SavePageAsync`) never change an existing page's parent/position; only
  `ReorderPageAsync` does, so a page can't be silently re-parented with stale sibling
  ordering by an unrelated content edit.
- `WikiDatabase`/`WikiDatabaseProperty`/`WikiDatabaseRow`/`WikiDatabaseView`
  (`src/GwsBusinessSuite.Application/Wiki/WikiDatabaseModels.cs`,
  `src/GwsBusinessSuite.Infrastructure/Services/WikiDatabaseService.cs`) are Notion-style
  typed records: one JSON blob per row (`PropertyValuesJson`, keyed by property id) rather
  than a normalized property-value table, same complexity tier as the page block model.
  `WikiDatabaseViewLogic` (filter/sort/`GroupForBoard`) is a pure, DB-free function set over
  an already-loaded row list — same split as `WikiBlockHtmlRenderer` vs. `WikiService`.
  Databases slot into the *same* sidebar tree as pages (`ParentWikiPageId`). Rows open as
  full block pages. Both `linked_database` and `inline_database` page blocks reference an
  existing database by id without copying its schema or rows; inline blocks expose the
  canonical typed cells and row creation directly in the document.
- Board view's drag-and-drop is native HTML5 DnD wired directly in Blazor
  (`@ondragstart`/`@ondragover:preventDefault`/`@ondrop` in `WikiDatabaseEditor.razor`) plus
  the existing global `wwwroot/js/dragReorder.js` shim (12 lines; only job is
  `dataTransfer.setData()` so Chromium continues the drag) — the same pattern the CMS
  Builder's Layers panel already uses, not a new JS module.

## Capability matrix

| Capability family | Foundation status | Expansion target |
| --- | --- | --- |
| Page model | Nested pages (flat parent-id + explicit sibling `SortOrder`), icon, cover image, move/reorder, transactional subtree duplication | — |
| Block editor | Slash-command insert, drag-reorder, Tab/Shift-Tab indent, inline bold/italic/link, `[[Page]]` autocomplete, reusable page and block templates, native tables, equations, breadcrumbs, TOC, buttons, synced blocks, columns, a contextual block menu (duplicate/move/delete, mirrored by keyboard shortcuts), session-local undo/redo, and Arrow-key/Escape keyboard navigation between blocks and out of floating menus | Character-level undo/redo persisted across reloads; keyboard flows for table-cell editing |
| Core block types | paragraph, heading 1-3, lists, to-do, toggle, quote, callout, code, divider, image, embed (YouTube/Vimeo/Spotify/Figma/CodePen/Loom render as a sandboxed iframe via URL-pattern detection, `WikiEmbedResolver.cs`; a Notion-imported video/audio block renders an inline `<video>`/`<audio>` player instead, via the same block's `mediaKind` prop; other URLs fall back to a plain link), table (rich per-cell formatting via `tableJson`), equation, breadcrumb, TOC, button, synced block, columns, and legacy markdown | True oEmbed (title/thumbnail metadata, live provider discovery) and additional provider-specific media |
| History | Bounded DB snapshot revisions (20/page), structural diff (added/removed/changed blocks), revert-as-new-version | — |
| Databases | Typed properties including person, files, place, evaluated advanced formulas, one-way or reciprocal row-picker relations, and calculated rollups; editable Table, Board, List, Gallery, Calendar, Timeline, Chart, Form, Map, Feed, and Dashboard views | Richer rollup formatting |
| Databases — structure | Databases share the page sidebar tree; every row opens as a responsive block-content page with a per-view side-peek, center-peek, or full-page presentation and configurable property visibility/order; row icon, cover image, and bounded version history (20/row, structural diff, revert-as-new-version) match page-level history; linked and inline database blocks reference canonical data without duplication | — |
| Search & graph | All-token ranked page/block/database-row search with highlighted matches; per-user saved searches; structured and legacy backlinks; per-user favorites/recents; structured page, person, date, and database-row mentions, with a personal mention inbox for people and a "Mentioned in" backlinks panel for rows | Graph visualization |
| Import/sync | Current `2026-03-11` Notion API, public-connection OAuth with encrypted access/refresh tokens and revocation (plus internal/PAT fallback), signed/deduplicated connection webhooks, data sources, views, comments, subtree-aware selective import with a browsable collapsible page/database picker, recursive template/unsupported-container content recovery, full-page Markdown recovery for unavailable structured content, meeting-note summary/notes/transcript import, incremental job state and content-count diagnostics, soft archival, imported page emoji/cover presentation, visible working child-page links, inline database/board blocks, durable authenticated copies of Notion-hosted files, field-level conflict review, explicitly enabled page and typed database-row pushes, reusable template import from connected free Notion templates or Markdown/CSV/HTML ZIP exports, full-fidelity property mapping (people/files/relation/created-by/last-edited-by/last-edited-time target Sentinel's real typed properties instead of degrading to text, with relation values resolved to local row ids), rich linked column layouts, inline video/audio players, and rich table cell formatting | Formula translation and schema mutation remain intentionally outside the safe v1 write contract |
| Visibility | Authenticated portal-member roles and per-resource view/comment/edit/full-access grants, plus expiring/revocable/password-protected tokenized public page and database shares | Per-share access-level controls and share auditing |

## Delivery sequence

1. **Block editor & page foundation** (delivered): structured block model replacing the
   Markdown string, slash-command block editor, drag-reorder + indent nesting, inline
   formatting, true collapsible page tree with move-up/down and reparenting, DB-snapshot
   history, page icon/cover, one-time Markdown-to-legacy-block backfill for pre-existing
   pages. Git/LibGit2Sharp removed.
2. **Databases** (delivered foundation):
   typed-property records with a property editor, Table view with inline-editable cells,
   Board view grouped by a Select property with native-HTML5-DnD card reordering across
   columns, plus List, Gallery, and Calendar views. Rows now open as pages with their own structured
   block content, and imported Notion database-row page bodies sync into those blocks.
3. **Notion API import/sync** (delivered): a `NotionConnectorSettings` singleton
   (OAuth access/refresh tokens or a manual integration token encrypted via
   `ISecretProtector`) + a typed-HttpClient
   `NotionService` pinned to `2026-03-11` + a `NotionSyncBackgroundService` (matching `CjAdsSyncBackgroundService`'s
   interval/semaphore/scope-per-tick shape); maps Notion's ~30 block types onto
   `WikiBlock.Type` and its ~22 database property types onto the Phase 2 property model;
   upsert-by-Notion-id reconciliation with soft-flagging of upstream-archived content,
   selected-id scopes, view and comment import, and guarded manual page writes. The Sentinel UI provides connection settings, a metadata-only
   browse step with a collapsible checkbox tree for selecting shared pages/databases (a selected
   parent includes its descendants), manual sync,
   hourly auto-sync control, last-sync counts, source badges, and dimmed-but-openable archived
   items. Manual imports are queued on the server and expose observable run status, so a browser,
   desktop WebView, or mobile connection interruption cannot cancel the workspace import.
   Sync-driven page changes deliberately do not create interactive revision snapshots,
   preventing hourly sync noise from evicting authored changes from the 20-revision history.
4. **Sentinel identity, search, and knowledge graph** (delivered): Sentinel product naming
   and canonical route; prominent `Command/Ctrl+Shift+F` all-token ranked page/block/file/database-row search with matched-term
   highlighting; page backlinks; durable per-user favorites and recents; `[[Page]]` page
   mentions; and `@` autocomplete for structured people/date mentions with a personal inbox.
5. **Database pages and complete views** (delivered): row block-content pages, linked and
   inline databases, expanded property vocabulary, and Table, Board, List, Gallery, Calendar,
   Timeline, Chart, Form, Map, Feed, and Dashboard views. Formula properties evaluate typed
   expressions over row values with cycle/error handling, arithmetic and logical operators, and
   numeric, text, conditional, and date functions exposed through an in-editor reference;
   Relation properties select canonical rows from another (or the same) database and can create
   a paired reciprocal property whose values remain synchronized from either side; and Rollup properties calculate count, numeric,
   and unique-value aggregates. Computed values remain derived rather than being persisted.
6. **Collaboration** (delivered foundation): authenticated page and block discussion threads, nested
   reply targets, resolve/reopen, emoji reactions, `@username` notification fan-out, and a
   personal read/unread notification panel, live cross-circuit discussion/notification
   refresh, heartbeat-expiring per-page presence, and editor-canvas block discussion pins are
   delivered. Atomic content-generation checks now reject stale saves and
   preserve the local draft with explicit reload, overwrite, or save-as-copy recovery choices.
   Concurrent saves now use block-identity three-way merge, automatically combining edits to
   different blocks while surfacing genuine same-block conflicts. Presence leases and discussion
   polling are database-backed, so they work across web instances. This is block-granular
   simultaneous editing, not character-level CRDT/OT cursor co-authoring. A lighter,
   in-memory-only `SentinelCursorTracker` now shows which block each other connected viewer is
   currently in (a colored name-pill, not a character-position caret) - see the "Real-time
   collaboration" delivery-plan entry below for the deliberate scope line between that and true
   concurrent text editing.
7. **Templates, sharing, and workspace structure** (delivered foundation): reusable page templates are
   delivered as durable snapshots that survive source-page deletion and create pages with fresh
   block identities. Page move/reorder and transactional subtree duplication are also delivered;
   duplicates receive fresh block identities and independent revision history. Workspace roles,
   granular page/database permissions, and expiring or revocable public shares are also delivered.
   Full database duplication now creates an adjacent independent copy with fresh property,
   row, view, and block identities while preserving remapped values and view configuration.
   Database templates are durable, source-independent snapshots of properties, rows, row-page
   blocks, and views; every use remaps internal identities. Reusable block templates capture the
   live editor snapshot, survive source-page deletion, and remap every block identity on insertion.
8. **Sentinel AI** (delivered foundation): Ollama-backed ask, summarize, rewrite, translate,
   research, meeting-notes, and database-autofill actions grounded in workspace pages and
   databases. Outputs are durable, reviewable runs and require approve/reject before insertion.
9. **Notion interoperability parity** (delivered foundation): current versioned data-source API,
   view and comment import, selective sync, and opt-in manual two-way page pushes with a
   remote-last-edit conflict guard.

## Full-clone parity contract

“Clone of Notion” means Sentinel is expected to implement the major workspace capabilities,
not stop at visual resemblance or import. Delivery is staged because database rows-as-pages,
permissions, collaboration, and concurrent editing affect the persistence model and must be
built in dependency order. Product-adjacent Notion apps are represented by equivalent Sentinel
capabilities where they fit GWS Business Suite; they are no longer silently excluded.

| Area | Delivered now | Required parity work |
| --- | --- | --- |
| Blocks | Core and advanced native block vocabulary, including tables/equations/columns/synced blocks/TOC/buttons, plus reusable block templates, and provider-pattern embeds (YouTube/Vimeo/Spotify/Figma/CodePen/Loom render as iframes) | True oEmbed metadata and additional providers |
| Databases | Eleven view families, expanded property vocabulary, advanced typed formulas, canonical one-way/reciprocal row relations, configurable rollups, filters/sorts/groups, row page bodies with per-view peek modes, linked/inline databases, reusable database templates, and a `database.rowChangedTrigger` node that starts a Workflow Automation when a database's row properties change | Property layouts and a write-back automation action node |
| Knowledge graph | `[[Page]]` links, ranked/highlighted workspace search, backlinks, person/date/database-row mentions, a row "Mentioned in" panel, favorites/recents, and per-user saved searches | Graph navigation |
| Collaboration | Discussions, replies, reactions, notifications, DB-backed cross-instance presence/polling, character-level remote selection indicators, automatically rebased selection anchors, block-level three-way merge, offline draft/reconnect replay, authenticated portal-member roles, granular permissions, and tokenized public sharing | A distributed CRDT/OT engine is an optional scale-out architecture, not part of the single-instance v1 acceptance contract |
| Presentation | Emoji icon and cover image for pages and database rows alike (cover can be uploaded inline via the cover picker, not just picked from an already-uploaded asset), row-level bounded version history, plus responsive side-peek, center-peek, and full-page database rows with per-view property presentation | Custom icon *image* uploads (icons remain emoji-only), page width/fonts, and reusable style defaults |
| Integration | Encrypted OAuth/token connection, current data-source/view/comment API, selective reconciliation, signed deduplicated webhooks, durable authenticated Notion-hosted file ingestion, field-level conflict review, opt-in page/database-row/new-property writes, and free-template interoperability through connected-workspace sync or bounded Notion ZIP imports | Notion formula translation and retyping/renaming an already-synced remote property remain intentionally outside the safe v1 write contract |
| AI | Workspace-grounded ask/writing/translation/research/meeting notes/autofill with streamed live output, ranked-search citations, and durable approve/reject runs | Transcription capture and autonomous agents |

Official research baseline: [Notion block API](https://developers.notion.com/reference/block),
[Notion API introduction](https://developers.notion.com/reference/intro),
[database views](https://www.notion.com/help/category/database-views/all),
[database rows as pages](https://www.notion.com/help/intro-to-databases), and
[comments](https://developers.notion.com/reference/comment-object). The connector pins
`2026-03-11` and uses `/v1/data_sources`, `/v1/views`, and `/v1/comments` rather than the
retired database-only contract.

Known import limitations are explicit: Notion-hosted files larger than 25 MB remain linked
to their temporary upstream URL rather than copied into Sentinel; `formula`/`rollup`
properties stay read-only best-effort text rather than native computed properties, since
faithfully importing one means translating Notion's own formula syntax into Sentinel's
expression language (and, for rollup, an already-imported relation to aggregate over) - a
separate project from a value-mapping fix; other uncommon property types (`phone_number`,
`email`, `unique_id`, `verification`, `button`) are likewise preserved as read-only best-effort
text, with `place` limited by the upstream API as well; `column_list`/`column` import into a
single native Columns block with per-column rich text and working imported child-page links.
The column container remains a flatter model than Notion's fully nested arbitrary block tree,
so separate complex blocks inside one column share one editable rich-text surface; and `link_to_page`
blocks surface as visible placeholder text referencing the linked Notion id rather than a
real Sentinel wikilink (unlike `[[Page]]` mentions, resolving this would need a second
DB-aware resolution pass, which isn't built for this narrower case). Relation property
*values*, `people`/`files`/`created_by`/`last_edited_by` property *values*, and
`last_edited_time` all now target Sentinel's real typed properties (Person/Files/Relation/
Date) instead of degrading to one flattened text string - relation values specifically
resolve from raw Notion page ids to local `WikiDatabaseRow` ids once the target row has been
imported (`NotionSyncService.ResolveRelationRowIdsAsync`, re-attempted every sync so a
relation to content imported later still resolves on a subsequent pass). When structured
block retrieval is unavailable, Sentinel requests Notion's full-page Markdown representation
and converts its common headings, paragraphs, lists, tasks, quotes, images, dividers, and
code fences into native editable blocks. The sync result reports imported block and
still-empty page counts so metadata-only imports are visible rather than silent.

Known UI/CSS gaps are also explicit, since a "delivered" line in this doc records the data
model and service layer being complete, not a guarantee the Razor/CSS layer keeps pace - the
row-peek panel above shipped with *zero* matching CSS for its own markup for some time (a dead,
unused `.sentinel-row-page-modal` rule was the only trace) until this pass added it. A 2026-07-24
audit of every Sentinel/Wiki `.razor` file against `app.css` found this was largely isolated
(Table/Board/List/Gallery/Calendar and the newer Timeline/Chart/Form/Map/Feed/Dashboard views are
all fully themed), but two real gaps remain open: `SentinelTemplates.razor` (and to a lesser
extent `SentinelDiscussions.razor`/`SentinelSharePanel.razor`) lean on raw Bootstrap
card/list-group/badge markup with only reactive `!important` overrides rather than a purpose-built
`sentinel-*` layout, so they read as generic admin chrome rather than the warm stone/amber
low-chrome document workspace; and `SentinelDiscussions`/`SentinelPresence`'s *base* CSS rules
(`app.css` ~1541-1566) are light-mode Bootstrap-indigo, only corrected by a `.sentinel-workspace`
descendant override further down the file - safe today because every usage site is nested inside
`.sentinel-workspace`, but silently wrong if either component is ever reused outside it (a share
surface, an embed). Neither blocks functionality; both are visual-polish debt.

## Remaining delivery plan

Sentinel's remaining Notion-class work is delivered in the following dependency order. Each
phase is a complete, releasable feature iteration rather than a collection of unfinished
screens.

1. **Database page layouts and presentation** ✅ — configurable side peek, center peek, and
   full-page row opening; responsive row pages; property presentation controls; row icon,
   cover image (picked from the existing Media Library, matching the page-cover picker), and
   bounded version history (20/row, structural diff, revert-as-new-version, via a new
   `WikiDatabaseRowRevision` table mirroring `WikiPageRevision`) are delivered - see
   `SentinelDatabaseRowPage.razor` and `WikiDatabaseService.GetRowHistoryAsync`/
   `GetRowStructuralDiffAsync`/`RevertRowToRevisionAsync`. A revision is only snapshotted when
   a save includes the page body (opened as a page), not on property-only cell edits or board
   drags, to avoid history noise - same rationale as Notion sync not creating page revisions.
2. **Native editing polish** ✅ (foundation) — database-page autosave is delivered:
   `SentinelDatabaseRowPage.razor` debounces edits (1.5s) into a silent `SaveRowAsync` call with
   the new `WikiDatabaseRowEditor.CreateRevisionCheckpoint = false`, so content, icon, and cover
   changes persist continuously without minting a version-history entry per keystroke burst -
   only the explicit "Save page" button (or a revert) still creates a checkpoint, matching how
   Notion separates continuous saving from coarser version history. A visible status line
   ("Editing…" / "Saved" / a retry prompt on failure) replaces the old static "saved on click"
   text. Undo/redo (`Ctrl`/`Cmd+Z`, `Ctrl`/`Cmd+Shift+Z`) and a contextual block menu (⋮:
   duplicate, move up/down, delete - each mirrored by a keyboard shortcut) were added to the
   *shared* `wiki-block-editor.js` module, so both the row editor and the main Sentinel page
   editor (`Wiki.razor`) gained them together rather than duplicating the feature. Undo history
   is session-local (an in-memory stack per open editor instance, not persisted) and is reset
   whenever the document is replaced wholesale (initial load, revert-to-revision) rather than
   trying to merge with server-driven changes. Keyboard-first flows gained Arrow-key navigation
   between blocks and Escape-to-close-menus; NOT yet covered: keyboard flows inside table cells,
   and a full command-palette-style shortcut reference.
3. **Media and presentation** ✅ (partial) — the "embed" block now recognizes YouTube, Vimeo,
   Spotify, Figma, CodePen, and Loom URLs (`WikiEmbedResolver.cs`, mirrored in
   `wiki-block-editor.js`'s `resolveEmbedUrl` for live-preview parity) and renders them as a
   sandboxed iframe instead of a plain link; any other URL still falls back to the existing
   link render. This is deliberately *not* full oEmbed - there is no server-side fetch of a
   provider's oembed/metadata endpoint (no title, no thumbnail, no live provider discovery),
   because that would mean fetching an arbitrary URL an editor pasted in, an SSRF surface this
   app doesn't have anywhere else. Every provider pattern is anchored to a fixed hostname
   instead. Cover images can now be uploaded inline from the page/row editor's cover picker
   (`InputFile` → `IMediaLibraryService.UploadAsync`, the same path `Media.razor` already used)
   rather than requiring a trip to the Media Library first. Deliberately NOT done: custom
   *icon* image uploads (icons remain emoji-only text - turning them into images would touch
   every place `Icon` is rendered as literal text, e.g. the sidebar tree and breadcrumbs, which
   is a wider ripple than this pass scoped), page width/font controls, and reusable
   workspace-level visual defaults (no settings entity for either exists yet).
4. **Database automations** ✅ (trigger foundation) — rather than building a second, parallel
   automation system, this integrates with the existing n8n-class Workflow Automation engine
   (`docs/WORKFLOW_AUTOMATION.md`), which already covers "schedules" (`core.scheduleTrigger`)
   and "approved integrations" (credential-gated nodes like `core.httpRequest` via
   `IAutomationCredentialService`) generically - neither needed anything Sentinel-specific. What
   was missing was a way for a workflow to react to a Sentinel database at all: a new
   `database.rowChangedTrigger` node (`AutomationNodeRegistry.cs`) whose `wikiDatabaseId`
   parameter is synced onto a new `AutomationWorkflow.TriggerWikiDatabaseId` column on Publish
   (`AutomationWorkflowService.PublishAsync`, mirroring exactly how `WebhookPath`/
   `ScheduleIntervalMinutes` are already synced from their own trigger nodes). Saving a
   Sentinel database row (`WikiDatabaseService.SaveRowAsync`) now calls
   `IAutomationTriggerService.TriggerDatabaseRowChangedAsync`, which runs every *active*
   workflow subscribed to that database - but only when a property value actually changed
   (new row, or an inline cell/property edit), not on a body/icon/cover-only save, so autosave
   ticks and content-only saves don't spuriously fire automations. A single subscribed
   workflow's own failure is caught and logged per-workflow so it can never block the row save
   that triggered it, nor stop sibling workflows from still running. Execution history needed
   no new UI: because this reuses `IAutomationExecutionService.ExecuteAsync` under a new
   `AutomationExecutionModes.DatabaseTrigger` mode, database-triggered runs already show up in
   the automation editor's existing Executions tab alongside manual/webhook/schedule runs.
   Deliberately NOT done: a write-back *action* node (e.g. "set a database row's property")
   for the other half of "trigger/action rules" - reacting to Sentinel is delivered, writing
   back to it from a workflow is not yet.
5. **Real-time collaboration** ✅ (remote cursors) — `SentinelCursorTracker`
   (`SentinelRealtimeCollaboration.cs`) is a new in-memory, process-local singleton tracking
   "which block is each other viewer currently in" per page, keyed by username with a 10s TTL.
   The block editor's `focusin` event (bubbling from whichever contenteditable the user
   actually clicked/tabbed into) calls back to Blazor via a new `OnCursorMoved` JSInvokable;
   `Wiki.razor` records it and re-broadcasts to every other circuit viewing that page through
   the tracker's `Moved` event (the same process-local fan-out `SentinelCollaborationNotifier`
   already uses for discussions/notifications - no SignalR hub exists or was added). Rendering
   is a new `setRemoteCursors` export in `wiki-block-editor.js`, a small colored name-pill
   pinned to a block's corner, following the exact same "look up state, targeted per-block DOM
   update" shape as the existing `setDiscussionCounts`. This is deliberately block-granular,
   not a character-offset caret - and it is deliberately the *entire* scope of what shipped
   here. Character-level concurrent editing needs a real OT or CRDT text-merge algorithm; that
   is a fundamentally different, much larger, and much riskier problem (a subtly wrong
   implementation corrupts user content, which is worse than not having the feature) than
   showing where people are, and remains explicitly NOT done. "Conflict-safe persistence" and
   "reconnect recovery" are already substantially covered by existing pieces rather than new
   ones: block-level three-way merge (`WikiBlockMerge.ThreeWayMerge`) already handles concurrent
   saves at block granularity, Blazor Server's own circuit-resume already recovers a brief
   disconnect with no data loss (the in-memory editor state was never lost), and a longer
   disconnect that forces a full reload now loses at most the last ~1.5s of unsaved edits
   thanks to Phase 2's row autosave - the equivalent for the main page editor (autosave, not
   just revert-safe reload) remains open.
6. **Knowledge navigation** ✅ (saved searches, database-row mentions) — **saved searches**: a
   new `SentinelSavedSearch` entity (Username, Query) lets a user bookmark the current search
   from a new button next to the search box; a "Saved searches" section in the sidebar
   (alongside the existing Favorites/Recents/Mentions) re-runs or deletes one. Saving the same
   query twice for the same user is a no-op rather than a duplicate row.
   **Database-row mentions**: `@` autocomplete now also suggests database rows by their title
   property (`SearchMentionSuggestionsAsync`'s new "row" branch), inserted as a
   `rowmention:{databaseId}:{rowId}` link - reusing the *exact* rich-text link scheme and
   `insertMention` JS logic `usermention:`/`datemention:` already use, so no new editor
   infrastructure was needed, only a new `WikiBlockHtmlRenderer.RenderRichText` prefix check.
   Interpreting "in the personal inbox" for a row (which, unlike a person, cannot receive a
   notification): the row's own page (`SentinelDatabaseRowPage.razor`) gained a "Mentioned in"
   panel listing every page that references it (`GetRowMentionsAsync`), mirroring the existing
   page-to-page Backlinks panel exactly, including its same known scan-scope limit (a mention
   written inside another row's body isn't picked up, only page bodies are scanned - matching
   `GetBacklinksAsync`'s existing behavior rather than quietly giving rows deeper scanning than
   pages get). **Graph navigation** (an interactive node/link visualization of the whole
   workspace) is explicitly NOT attempted here - unlike the other two items, it has no existing
   code to reuse or extend and is a substantial visual/canvas feature in its own right, so it
   remains fully open as its own future unit of work.
7. **Sentinel AI depth** ✅ (streaming + citations) — `IOllamaService` gained
   `GenerateStreamAsync` (Ollama's NDJSON `stream:true` mode; no streaming precedent existed
   anywhere in this app before this), and `SentinelAiPanel.razor` now renders tokens live as
   they arrive instead of a spinner-then-full-result. Grounding was rebuilt on top of
   `SentinelWorkspaceService.SearchAsync` (ranked top-K, same search the sidebar box uses)
   instead of dumping the 30 most-recent pages and 12 databases into every prompt regardless
   of relevance; each run now persists a `CitationsJson` list of exactly which pages/databases
   were folded into its context, shown as source chips under the answer. Known limitation
   surfaced by this: `SearchAsync`'s matching is an AND over every instruction token including
   stopwords, so a natural-language question ("what does the blue switch do?") often cites
   nothing even when a clearly relevant page exists, while a keyword-style instruction ("blue
   switch") does - citations are best-effort, not a guarantee of complete grounding coverage.
   Deliberately NOT attempted: **transcription capture** (Ollama serves LLMs, not speech-to-
   text; this needs an entirely separate self-hosted ASR tool such as whisper.cpp, which is a
   new infrastructure/deployment dependency, not something to add unilaterally without that
   being an explicit decision) and **reviewable autonomous workflows** (chaining multiple AI
   steps with human checkpoints between them - every `SentinelAiRun` today is still strictly
   one-shot; the automation engine's `core.wait`/`core.approval` pause-resume pattern is a
   plausible template for this but was not built).
8. **Sync and sharing hardening** ✅ (partial) — database schema pushes and password-protected
   public shares are delivered. `NotionSyncService.PushDatabaseSchemaAsync` (paired with a new
   `NotionService.UpdateDataSourcePropertiesAsync`, `PATCH data_sources/{id}`) pushes only
   locally-created properties (`WikiDatabaseProperty` rows with no `NotionId` yet) to Notion as
   brand-new data-source properties, gated behind the same two-way-write opt-in and
   remote-last-edit conflict guard as `PushPageAsync`/`PushDatabaseRowAsync`. It deliberately
   never renames or retypes a property that already exists in Notion - only ever adds - so a
   local edit can't unilaterally reshape the user's real Notion workspace. Supported types are
   Text/Url/Number/Checkbox/Date/Select/MultiSelect; Relation (needs a resolved remote
   data-source id), Formula/Rollup (would need Notion's own formula syntax - see the "Known
   import limitations" note below), and Person/Files/Place/CreatedTime (no schema shape this
   app can create unilaterally) are explicitly skipped with a per-property reason surfaced in
   the result message. After a successful push, Notion's assigned property id is matched back
   onto the local property by name and stored as its `NotionId`, so a later row-value push can
   target it. A "Push N new properties to Notion" button appears in the database's Properties
   panel (`WikiDatabaseEditor.razor`) whenever any exist. **Public shares** gained optional
   password protection: `SentinelPublicShare.PasswordSalt`/`PasswordHash` (salted, since unlike
   `TokenHash` - which hashes an already-high-entropy random token - a share password is
   user-chosen and low-entropy). `SentinelSharePanel.razor`'s "Create link" flow accepts an
   optional password; the public route (`SentinelPublicShare.razor`) gates page/database
   content behind a password prompt via a new `ISentinelAccessService.VerifySharePasswordAsync`
   before `ResolvePublicShareAsync`'s target is loaded, kept deliberately simple (re-prompts on
   every fresh page load - no session/cookie persistence layer was added). Durable Notion-hosted
   file ingestion is delivered. **Not done**: retyping/renaming already-synced Notion
   properties, a per-share access-level selector (public shares remain always-read-only), and
   share view/access analytics.
9. **Native platform integrations** — platform-appropriate desktop and mobile affordances.
   On macOS this includes an optional persistent Sentinel menu-bar companion with a dedicated
   original Sentinel logo, quick actions to open Sentinel or the main dashboard, refresh
   workspace data, and quit the desktop app. The normal Mac app continues to open the complete
   admin portal; Sentinel remains one workspace inside it.

## Safety rules

- Sentinel content never stores plaintext secrets.
- Notion OAuth access and refresh tokens follow this app's encrypted-secret convention and are
  never returned to the browser. Authorization state is protected, short-lived, and bound to
  the signed-in admin. Manual internal/PAT tokens remain an advanced fallback.
- Server-side authorization remains authoritative for `/admin/sentinel`; public access is
  isolated to random-token `/sentinel/share/{token}` routes. Only token hashes are stored,
  and expiry/revocation is checked server-side on every resolution.
- Block rich text only ever contains four inline tags (`b`/`strong`, `i`/`em`, `code`, `a`)
  produced by the editor's own formatting commands — pasted content is stripped to plain
  text, not sanitized-and-kept, to avoid needing an HTML allowlist sanitizer.
