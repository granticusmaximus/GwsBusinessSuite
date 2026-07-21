# Wiki: Notion-style workspace

The GWS Business Suite Wiki is being rebuilt from a single-Markdown-string-per-page wiki
into a Notion-style block-based workspace: nested pages with a real block editor, and
(in a later phase) Notion-style databases. It does not copy Notion source code, UI assets,
trademarks, or proprietary schemas — the block/property type vocabulary below is a
clean-room adaptation informed by Notion's public API documentation as behavioral
research only.

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
| Block editor | Slash-command insert, drag-reorder, Tab/Shift-Tab indent, inline bold/italic/link (Ctrl+B/I/K), `[[Page]]` autocomplete, paste-as-plain-text | Nested columns, synced/reusable blocks, comments, mentions |
| Core block types | paragraph, heading 1-3, bulleted/numbered list item, to-do, toggle, quote, callout, code, divider, image, embed, legacy markdown (pre-migration content) | table, richer embeds (oEmbed previews) |
| History | Bounded DB snapshot revisions (20/page), structural diff (added/removed/changed blocks), revert-as-new-version | — |
| Databases | Typed properties (title, text, number, select, multi-select, date, checkbox, url, created-time), Table view (inline-editable cells), Board view (grouped by a Select property, native-HTML5-DnD reordering across columns) | Calendar view, Gallery view, formula/relation/rollup properties, person/files properties |
| Databases — structure | Databases share the page sidebar tree, move-up/down + reparent like pages | Inline-embedded databases within a page's blocks, rows that open as full sub-pages |
| Import/sync | Delivered: read-only live Notion API import via a pasted, encrypted internal-integration token; manual/hourly sync; upsert-by-Notion-id reconciliation; hierarchy, blocks, database schema/rows, and soft archival | Two-way/conflict-aware sync and selective per-page sync are intentionally out of scope |
| Visibility | Admin-only (`/admin/wiki`), same as before | Public-facing wiki view |

## Delivery sequence

1. **Block editor & page foundation** (delivered): structured block model replacing the
   Markdown string, slash-command block editor, drag-reorder + indent nesting, inline
   formatting, true collapsible page tree with move-up/down and reparenting, DB-snapshot
   history, page icon/cover, one-time Markdown-to-legacy-block backfill for pre-existing
   pages. Git/LibGit2Sharp removed.
2. **Databases** (delivered — Table + Board; Calendar/Gallery follow-up not yet started):
   typed-property records with a property editor, Table view with inline-editable cells,
   Board view grouped by a Select property with native-HTML5-DnD card reordering across
   columns. Calendar view (no existing UI pattern anywhere in this codebase to build from —
   scoped out of this pass) and Gallery view (trivial once this data model exists — a
   near-copy of the Media Library's CSS-grid card list) are the next slice, along with
   inline-embedded databases and rows-as-full-pages.
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
4. **Polish/parity**: backlinks/mentions, full-text search, page templates, comments,
   formula/relation/rollup database properties, a public-facing wiki view.

## Phase 3 parity boundary

Phase 3 is a practical read-only workspace mirror, not a claim of complete Notion product
parity. The useful follow-ups are separated from features that do not fit this admin-only
internal Wiki.

| Area | Delivered now | Worth a focused follow-up | Permanently outside this clone's current scope |
| --- | --- | --- | --- |
| Blocks | Core text/list/task/toggle/callout/code/media/embed blocks; tables collapse to Markdown; layout wrappers flatten | Equation/KaTeX and a native table block if imported workspaces need them | AI meeting notes/transcription and unsupported placeholders |
| Databases | Table/Board views; first-class common properties; other API values retained as best-effort text | Calendar/Gallery/List views, then formula/relation/rollup and person/files types where editing semantics are defined | Notion Projects, Calendar, and Mail as separate products |
| Collaboration | Existing app-level admin authorization | Backlinks/mentions, comments, and possibly a public read-only Wiki | Reproducing Notion's workspace/teamspace/guest/share-link permission matrix or real-time collaboration |
| Presentation | Emoji icon and cover URL | Custom uploads or richer icon selection | Notion's template marketplace and proprietary design assets |
| Integration | One-way API import, encrypted pasted token, manual/hourly reconciliation | Optional per-page selection and durable re-hosting for expiring Notion-hosted images | Notion writes, two-way conflict merging, OAuth, marketplace connections, MCP/agent integrations, and Notion AI |

Known import limitations are explicit: Notion-hosted file image URLs can expire; relation
values retain related-page ids rather than resolved titles; `equation`, `breadcrumb`,
`table_of_contents`, `meeting_notes`, and `transcription` blocks are skipped; uncommon or
computed property types are preserved as read-only best-effort text, with `place` limited by
the upstream API as well.

## Safety rules

- Wiki content never stores plaintext secrets.
- The Notion integration token follows this app's existing convention: pasted into a
  settings form, encrypted at rest via `ISecretProtector`, never an OAuth flow (this app
  has none).
- Server-side authorization remains authoritative for `/admin/wiki`; there is no public
  route yet.
- Block rich text only ever contains four inline tags (`b`/`strong`, `i`/`em`, `code`, `a`)
  produced by the editor's own formatting commands — pasted content is stripped to plain
  text, not sanitized-and-kept, to avoid needing an HTML allowlist sanitizer.
