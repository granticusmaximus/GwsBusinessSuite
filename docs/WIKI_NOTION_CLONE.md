# Sentinel: Notion-class connected workspace

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
| Core block types | paragraph, heading 1-3, lists, to-do, toggle, quote, callout, code, divider, image, embed (YouTube/Vimeo/Spotify/Figma/CodePen/Loom render as a sandboxed iframe via URL-pattern detection, `WikiEmbedResolver.cs`; other URLs fall back to a plain link), table, equation, breadcrumb, TOC, button, synced block, columns, and legacy markdown | True oEmbed (title/thumbnail metadata, live provider discovery) and additional provider-specific media |
| History | Bounded DB snapshot revisions (20/page), structural diff (added/removed/changed blocks), revert-as-new-version | — |
| Databases | Typed properties including person, files, place, evaluated advanced formulas, one-way or reciprocal row-picker relations, and calculated rollups; editable Table, Board, List, Gallery, Calendar, Timeline, Chart, Form, Map, Feed, and Dashboard views | Richer rollup formatting |
| Databases — structure | Databases share the page sidebar tree; every row opens as a responsive block-content page with a per-view side-peek, center-peek, or full-page presentation and configurable property visibility/order; row icon, cover image, and bounded version history (20/row, structural diff, revert-as-new-version) match page-level history; linked and inline database blocks reference canonical data without duplication | — |
| Search & graph | All-token ranked page/block/database-row search with highlighted matches; structured and legacy backlinks; per-user favorites/recents; structured page, person, and date mentions with a personal mention inbox | Graph visualization, saved searches, and database-row mention inbox entries |
| Import/sync | Current `2026-03-11` Notion API, data sources, views, comments, subtree-aware selective import, recursive template/unsupported-container content recovery, full-page Markdown recovery for unavailable structured content, meeting-note summary/notes/transcript import, content-count diagnostics, encrypted token storage, soft archival, durable authenticated copies of Notion-hosted files, explicitly enabled conflict-aware manual page pushes, and reusable template import from connected free Notion templates or Markdown/CSV/HTML ZIP exports | Broader bidirectional database writes |
| Visibility | Authenticated portal-member roles and per-resource view/comment/edit/full-access grants, plus expiring/revocable tokenized public page and database shares | Richer public-share controls and auditing |

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
   (encrypted integration token via `ISecretProtector`, matching `CjConnectorSettings`) + a typed-HttpClient
   `NotionService` pinned to `2026-03-11` + a `NotionSyncBackgroundService` (matching `CjAdsSyncBackgroundService`'s
   interval/semaphore/scope-per-tick shape); maps Notion's ~30 block types onto
   `WikiBlock.Type` and its ~22 database property types onto the Phase 2 property model;
   upsert-by-Notion-id reconciliation with soft-flagging of upstream-archived content,
   selected-id scopes, view and comment import, and guarded manual page writes. The Sentinel UI provides connection settings, manual sync,
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
   simultaneous editing, not character-level CRDT/OT cursor co-authoring.
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
| Knowledge graph | `[[Page]]` links, ranked/highlighted workspace search, backlinks, person/date mentions, favorites/recents | Graph navigation, database-row mention inbox entries, and saved searches |
| Collaboration | Discussions, replies, reactions, notifications, DB-backed cross-instance presence/polling, block-level three-way merge, authenticated portal-member roles, granular permissions, and tokenized public sharing | Character-level CRDT/OT cursors and richer public-share controls |
| Presentation | Emoji icon and cover image for pages and database rows alike (cover can be uploaded inline via the cover picker, not just picked from an already-uploaded asset), row-level bounded version history, plus responsive side-peek, center-peek, and full-page database rows with per-view property presentation | Custom icon *image* uploads (icons remain emoji-only), page width/fonts, and reusable style defaults |
| Integration | Encrypted token, current data-source/view/comment API, selective reconciliation, durable authenticated Notion-hosted file ingestion, opt-in conflict-aware manual page writes, and free-template interoperability through connected-workspace sync or bounded Notion ZIP imports | Bidirectional database schema/row writes |
| AI | Workspace-grounded ask/writing/translation/research/meeting notes/autofill with durable approve/reject runs | Streaming chat, citations, transcription capture, and autonomous agents |

Official research baseline: [Notion block API](https://developers.notion.com/reference/block),
[Notion API introduction](https://developers.notion.com/reference/intro),
[database views](https://www.notion.com/help/category/database-views/all),
[database rows as pages](https://www.notion.com/help/intro-to-databases), and
[comments](https://developers.notion.com/reference/comment-object). The connector pins
`2026-03-11` and uses `/v1/data_sources`, `/v1/views`, and `/v1/comments` rather than the
retired database-only contract.

Known import limitations are explicit: Notion-hosted files larger than 25 MB remain linked
to their temporary upstream URL rather than copied into Sentinel; relation values retain
related-page ids rather than resolved titles; uncommon or computed property types are
preserved as read-only best-effort text, with `place` limited by the upstream API as well.
When structured block retrieval is unavailable, Sentinel requests Notion's full-page Markdown
representation and converts its common headings, paragraphs, lists, tasks, quotes, images,
dividers, and code fences into native editable blocks. The sync result reports imported block
and still-empty page counts so metadata-only imports are visible rather than silent.

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
5. **Real-time collaboration** — character-level concurrent editing, remote selections and
   cursors, reconnect recovery, and conflict-safe persistence.
6. **Knowledge navigation** — graph navigation, saved searches, and database-row mentions in
   the personal inbox.
7. **Sentinel AI depth** — streaming answers with citations, transcription capture, and
   reviewable autonomous workflows.
8. **Sync and sharing hardening** — bidirectional database schema/row writes and richer
   public-share controls. Durable Notion-hosted file ingestion is delivered.
9. **Native platform integrations** — platform-appropriate desktop and mobile affordances.
   On macOS this includes an optional persistent Sentinel menu-bar companion with a dedicated
   original Sentinel logo, quick actions to open Sentinel or the main dashboard, refresh
   workspace data, and quit the desktop app. The normal Mac app continues to open the complete
   admin portal; Sentinel remains one workspace inside it.

## Safety rules

- Sentinel content never stores plaintext secrets.
- The Notion integration token follows this app's existing convention: pasted into a
  settings form, encrypted at rest via `ISecretProtector`, never an OAuth flow (this app
  has none).
- Server-side authorization remains authoritative for `/admin/sentinel`; public access is
  isolated to random-token `/sentinel/share/{token}` routes. Only token hashes are stored,
  and expiry/revocation is checked server-side on every resolution.
- Block rich text only ever contains four inline tags (`b`/`strong`, `i`/`em`, `code`, `a`)
  produced by the editor's own formatting commands — pasted content is stripped to plain
  text, not sanitized-and-kept, to avoid needing an HTML allowlist sanitizer.
