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
| Block editor | Slash-command insert, drag-reorder, Tab/Shift-Tab indent, inline bold/italic/link, `[[Page]]` autocomplete, reusable page and block templates, native tables, equations, breadcrumbs, TOC, buttons, synced blocks, and columns | Richer embeds |
| Core block types | paragraph, heading 1-3, lists, to-do, toggle, quote, callout, code, divider, image, embed, table, equation, breadcrumb, TOC, button, synced block, columns, and legacy markdown | oEmbed previews and additional provider-specific media |
| History | Bounded DB snapshot revisions (20/page), structural diff (added/removed/changed blocks), revert-as-new-version | — |
| Databases | Typed properties including person, files, place, formula, relation and rollup; editable Table, Board, List, Gallery, Calendar, Timeline, Chart, Form, Map, Feed, and Dashboard views | Formula evaluation and richer relation/rollup configuration |
| Databases — structure | Databases share the page sidebar tree; every row opens as a block-content page; linked and inline database blocks reference canonical data without duplication | Row covers/icons, page history, layouts and peek modes |
| Search & graph | All-token ranked page/block/database-row search with highlighted matches; structured and legacy backlinks; per-user favorites/recents; structured page, person, and date mentions with a personal mention inbox | Graph visualization, saved searches, and database-row mention inbox entries |
| Import/sync | Current `2026-03-11` Notion API, data sources, views, comments, selective import, encrypted token storage, soft archival, and explicitly enabled conflict-aware manual page pushes | Durable ingestion of expiring Notion-hosted files and broader bidirectional database writes |
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
   items. Sync-driven page changes deliberately do not create interactive revision snapshots,
   preventing hourly sync noise from evicting authored changes from the 20-revision history.
4. **Sentinel identity, search, and knowledge graph** (delivered): Sentinel product naming
   and canonical route; all-token ranked page/block/database-row search with matched-term
   highlighting; page backlinks; durable per-user favorites and recents; `[[Page]]` page
   mentions; and `@` autocomplete for structured people/date mentions with a personal inbox.
5. **Database pages and complete views** (delivered): row block-content pages, linked and
   inline databases, expanded property vocabulary, and Table, Board, List, Gallery, Calendar,
   Timeline, Chart, Form, Map, Feed, and Dashboard views.
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
| Blocks | Core and advanced native block vocabulary, including tables/equations/columns/synced blocks/TOC/buttons, plus reusable block templates | Richer embeds |
| Databases | Eleven view families, expanded property vocabulary, filters/sorts/groups, row page bodies, linked/inline databases, and reusable database templates | Formula computation, rich relation configuration, layouts, and automations |
| Knowledge graph | `[[Page]]` links, ranked/highlighted workspace search, backlinks, person/date mentions, favorites/recents | Graph navigation, database-row mention inbox entries, and saved searches |
| Collaboration | Discussions, replies, reactions, notifications, DB-backed cross-instance presence/polling, block-level three-way merge, authenticated portal-member roles, granular permissions, and tokenized public sharing | Character-level CRDT/OT cursors and richer public-share controls |
| Presentation | Emoji icon and cover URL | Custom icon/cover uploads, page width/fonts, database layouts, peek modes, and reusable style defaults |
| Integration | Encrypted token, current data-source/view/comment API, selective reconciliation, and opt-in conflict-aware manual page writes | Durable file ingestion and bidirectional database schema/row writes |
| AI | Workspace-grounded ask/writing/translation/research/meeting notes/autofill with durable approve/reject runs | Streaming chat, citations, transcription capture, and autonomous agents |

Official research baseline: [Notion block API](https://developers.notion.com/reference/block),
[Notion API introduction](https://developers.notion.com/reference/intro),
[database views](https://www.notion.com/help/category/database-views/all),
[database rows as pages](https://www.notion.com/help/intro-to-databases), and
[comments](https://developers.notion.com/reference/comment-object). The connector pins
`2026-03-11` and uses `/v1/data_sources`, `/v1/views`, and `/v1/comments` rather than the
retired database-only contract.

Known import limitations are explicit: Notion-hosted file image URLs can expire; relation
values retain related-page ids rather than resolved titles; `meeting_notes` and
`transcription` blocks are skipped; uncommon or
computed property types are preserved as read-only best-effort text, with `place` limited by
the upstream API as well.

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
