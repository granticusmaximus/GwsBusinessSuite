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
  canvas previews the live public-render route in an iframe; the Wiki editor has no such
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
  Databases slot into the *same* sidebar tree as pages (`ParentWikiPageId`) rather than
  living inside a page's block content — inline-embedded databases and "rows that open as
  full pages" are both deferred (see Delivery sequence).
- Board view's drag-and-drop is native HTML5 DnD wired directly in Blazor
  (`@ondragstart`/`@ondragover:preventDefault`/`@ondrop` in `WikiDatabaseEditor.razor`) plus
  the existing global `wwwroot/js/dragReorder.js` shim (12 lines; only job is
  `dataTransfer.setData()` so Chromium continues the drag) — the same pattern the CMS
  Builder's Layers panel already uses, not a new JS module.

## Capability matrix

| Capability family | Foundation status | Expansion target |
| --- | --- | --- |
| Page model | Nested pages (flat parent-id + explicit sibling `SortOrder`), icon, cover image | — |
| Block editor | Slash-command insert, drag-reorder, Tab/Shift-Tab indent, inline bold/italic/link (Ctrl+B/I/K), `[[Page]]` autocomplete, paste-as-plain-text | Nested columns, synced/reusable blocks, native tables, equations, breadcrumbs, TOC, buttons, templates |
| Core block types | paragraph, heading 1-3, bulleted/numbered list item, to-do, toggle, quote, callout, code, divider, image, embed, legacy markdown (pre-migration content) | table, richer embeds (oEmbed previews) |
| History | Bounded DB snapshot revisions (20/page), structural diff (added/removed/changed blocks), revert-as-new-version | — |
| Databases | Typed properties (title, text, number, select, multi-select, date, checkbox, url, created-time); editable Table and Board; List and Gallery views | Calendar, Timeline, Chart, Form, Map, Feed, and Dashboard views; formula/relation/rollup and person/files properties |
| Databases — structure | Databases share the page sidebar tree; every row opens as a block-content page | Inline/linked databases, row covers/icons, page history, layouts and peek modes |
| Search & graph | Workspace-wide page/block/database-row search; structured and legacy backlinks | Ranked token search, recent/favorites, graph navigation, user/date/page mentions |
| Import/sync | Delivered: read-only live Notion API import via a pasted, encrypted internal-integration token; manual/hourly sync; upsert-by-Notion-id reconciliation; hierarchy, blocks, database schema/rows, and soft archival | Upgrade from the pinned 2022 API to the current data-source/view API; selective and two-way conflict-aware sync |
| Visibility | Admin-only canonical route (`/admin/sentinel`), with `/admin/wiki` retained as an alias | Workspace/member/guest roles, page permissions, public share links |

## Delivery sequence

1. **Block editor & page foundation** (delivered): structured block model replacing the
   Markdown string, slash-command block editor, drag-reorder + indent nesting, inline
   formatting, true collapsible page tree with move-up/down and reparenting, DB-snapshot
   history, page icon/cover, one-time Markdown-to-legacy-block backfill for pre-existing
   pages. Git/LibGit2Sharp removed.
2. **Databases** (delivered foundation):
   typed-property records with a property editor, Table view with inline-editable cells,
   Board view grouped by a Select property with native-HTML5-DnD card reordering across
   columns, plus List and Gallery views. Rows now open as pages with their own structured
   block content, and imported Notion database-row page bodies sync into those blocks.
3. **Notion API import/sync** (delivered): a `NotionConnectorSettings` singleton
   (encrypted integration token via `ISecretProtector`, matching `CjConnectorSettings`) + a typed-HttpClient
   `NotionService` + a `NotionSyncBackgroundService` (matching `CjAdsSyncBackgroundService`'s
   interval/semaphore/scope-per-tick shape); maps Notion's ~30 block types onto
   `WikiBlock.Type` and its ~22 database property types onto the Phase 2 property model;
   upsert-by-Notion-id reconciliation with soft-flagging of upstream-archived content
   (not a destructive replace-all). The Wiki UI provides connection settings, manual sync,
   hourly auto-sync control, last-sync counts, source badges, and dimmed-but-openable archived
   items. Sync-driven page changes deliberately do not create interactive revision snapshots,
   preventing hourly sync noise from evicting authored changes from the 20-revision history.
4. **Sentinel identity, search, and knowledge graph** (in progress): Sentinel product naming
   and canonical route, workspace-wide page/block/database-row search, and page backlinks are
   delivered. Favorites, recents, stronger ranked/token search, and mentions remain.
5. **Database pages and complete views** (in progress): row block-content pages plus List and
   Gallery are delivered. Remaining work is linked/inline databases; Calendar, Timeline,
   Chart, Form, Map, Feed, and Dashboard views; view-specific layout/open mode; formulas,
   relations, rollups, people, and files.
6. **Collaboration**: page and inline/block discussion threads, replies, resolution, emoji
   reactions, user/page/date mentions, notifications, presence, and safe concurrent editing.
7. **Templates, sharing, and workspace structure**: page/database templates, duplicate/move,
   favorites/recents, teamspaces, member/guest roles, granular page/database permissions,
   and expiring public share links.
8. **Sentinel AI and automation**: workspace-grounded search/chat, writing and translation,
   database autofill/formulas, research mode, meeting-note transcription/summaries, and
   reviewable agents/automations using the app's existing Ollama and workflow infrastructure.
9. **Notion interoperability parity**: migrate the connector to the current API version and
   data-source/view model, import comments/views/templates and richer blocks, add selective
   sync, then add opt-in two-way writes with explicit conflict handling.

## Full-clone parity contract

“Clone of Notion” means Sentinel is expected to implement the major workspace capabilities,
not stop at visual resemblance or import. Delivery is staged because database rows-as-pages,
permissions, collaboration, and concurrent editing affect the persistence model and must be
built in dependency order. Product-adjacent Notion apps are represented by equivalent Sentinel
capabilities where they fit GWS Business Suite; they are no longer silently excluded.

| Area | Delivered now | Required parity work |
| --- | --- | --- |
| Blocks | Core text/list/task/toggle/callout/code/media/embed blocks; tables import as Markdown; layout wrappers flatten | Complete supported block vocabulary, native tables/equations/columns/synced blocks, reusable templates, and richer embeds |
| Databases | Editable Table/Board/List/Gallery, filters/sorts/groups, common property types, and rows with block page bodies | Remaining major view families, linked/inline sources, formulas/relations/rollups, layouts, charts, forms, and automations |
| Knowledge graph | `[[Page]]` links, workspace content search, backlinks | Mentions, favorites/recents, graph navigation, robust ranking/highlighting, and saved searches |
| Collaboration | Existing app authentication and revision history | Comments/discussions, reactions, presence, notifications, concurrent editing, workspace roles, granular permissions, and public sharing |
| Presentation | Emoji icon and cover URL | Custom icon/cover uploads, page width/fonts, database layouts, peek modes, and reusable style defaults |
| Integration | Encrypted token, one-way manual/hourly reconciliation | Current Notion data-source/view/comment API, selective sync, durable file ingestion, and opt-in conflict-aware writes |
| AI | Existing Ollama and workflow foundations elsewhere in the suite | Sentinel-grounded chat/search, writing, autofill, research, meeting notes, and reviewable workspace agents |

Official research baseline: [Notion block API](https://developers.notion.com/reference/block),
[Notion API introduction](https://developers.notion.com/reference/intro),
[database views](https://www.notion.com/help/category/database-views/all),
[database rows as pages](https://www.notion.com/help/intro-to-databases), and
[comments](https://developers.notion.com/reference/comment-object). The connector remains on
the older pinned API until the data-source migration is implemented and covered by fixtures;
changing only the version header would break rather than improve interoperability.

Known import limitations are explicit: Notion-hosted file image URLs can expire; relation
values retain related-page ids rather than resolved titles; `equation`, `breadcrumb`,
`table_of_contents`, `meeting_notes`, and `transcription` blocks are skipped; uncommon or
computed property types are preserved as read-only best-effort text, with `place` limited by
the upstream API as well.

## Safety rules

- Sentinel content never stores plaintext secrets.
- The Notion integration token follows this app's existing convention: pasted into a
  settings form, encrypted at rest via `ISecretProtector`, never an OAuth flow (this app
  has none).
- Server-side authorization remains authoritative for `/admin/sentinel`; there is no public
  route yet.
- Block rich text only ever contains four inline tags (`b`/`strong`, `i`/`em`, `code`, `a`)
  produced by the editor's own formatting commands — pasted content is stripped to plain
  text, not sanitized-and-kept, to avoid needing an HTML allowlist sanitizer.
