# Sentinel rebuild

## Product target

Sentinel is being rebuilt as a clean-room, Notion-class workspace with close functional and
interaction parity. “Parity” means familiar information architecture, editing behavior,
databases, keyboard flows, collaboration, and import fidelity inside Sentinel's own identity.
It does not mean copying Notion source code, trademarks, logos, proprietary assets, or private
implementation details.

The rebuild is additive and migration-safe. Existing Sentinel pages, databases, revisions,
permissions, imported files, and Notion identities remain the source of truth while UI surfaces
are replaced. Proven importer and reconciliation code is retained unless a replacement has a
measurable correctness advantage.

## Notion connection contract

Sentinel supports Notion public-connection OAuth as the primary setup path and retains internal
connection tokens and personal access tokens as an advanced fallback. OAuth access and refresh
tokens are encrypted at rest. Authorization state is integrity-protected, expires after ten
minutes, and is bound to the signed-in portal administrator. Disconnect revokes the token at
Notion before clearing the local encrypted copy.

OAuth is authorization, not credential sharing: Sentinel never receives or stores a user's
Notion password. The user signs in on Notion and chooses the pages the connection may access.
Notion's API does not offer unrestricted account export through OAuth, and its Search endpoint
is not guaranteed to exhaustively enumerate every accessible document. Sentinel therefore:

- recursively imports every authorized root it discovers and its accessible descendants;
- queries each authorized data source directly for rows;
- provides refresh/browse controls for Notion's asynchronous search index;
- reports discovered/imported/skipped coverage instead of claiming invisible content was synced;
- preserves ZIP workspace restore as the complete-export fallback for content the API cannot see.

## Delivery sequence

1. **Connection foundation — delivered.** OAuth authorize/callback, encrypted access and
   refresh tokens, rotation, revoke/disconnect, workspace identity, manual-token fallback, and
   additive migration.
2. **Workspace shell — delivered.** Focused three-pane workspace with collapsible private
   browser and page-context rail, favorites/private/teamspace sections, quick find, full
   ancestor breadcrumb, compact page chrome, responsive pane behavior, and keyboard navigation
   (`Ctrl/Command+Shift+F` for quick find, `Ctrl/Command+\` for the browser, and `Escape` to
   close the mobile browser). Existing page, database, collaboration, history, and Notion
   workflows remain on their original identifiers and services.
3. **Editor parity — release gate delivered.** Rich-text selection now round-trips bold, italic,
   strikethrough, code, links, safe text colors, and safe highlight colors through the editor,
   server renderer, Notion import, and Notion write-back. Bounded undo/redo history persists
   across page reopen and Blazor reconnect for the browser session. Nested blocks now behave
   as hierarchy-safe branches: split/insert, indent/outdent, duplicate, peer movement,
   drag/reorder, delete, and undo preserve every contiguous descendant while retaining the
   migration-safe flat `IndentLevel` representation. Slash commands, page links, and
   person/date/database-row mentions now share a grouped, descriptive, accessible listbox
   with active-option semantics, Arrow/Home/End navigation, Enter/Tab selection, Escape
   dismissal, responsive positioning, and stale async-result protection. Columns support
   add/remove/reorder controls, text selections create durable comment anchors, and safe local
   drafts recover across browser restarts when the server baseline is unchanged.
4. **Database parity — release gate delivered.** Inline and full-page databases share the
   database service; property editing, filters, multi-sort, board groups, table/list/board/
   gallery/calendar/timeline/chart/form/map/feed/dashboard layouts, templates, row peeks,
   per-view calculations, formulas, and relation/rollup editing are available.
5. **Collaboration and sharing — release gate delivered.** Presence, block cursors, anchored
   discussions, replies, reactions, notifications, granular permissions, guest-style resource
   access, expiring/indexable public shares, and auditable mutations are available.
6. **Sync completeness and observability — release gate delivered.** Incremental watermarks,
   server-owned retryable jobs, scoped discovery, discovered/imported/updated/skipped/empty/
   archived/block counters, OAuth rotation/revoke, ZIP fallback, and guarded page write-back
   are available. Webhook-triggered refresh and a richer field-level conflict-review surface
   remain post-gate enhancements.
7. **Quality gate — delivered.** Additive migrations, zero-warning Release build, full automated
   suite, editor browser regressions, real Blazor desktop/mobile review, and live migration
   rehearsal are complete for this release.

Each stage is independently shippable. A stage is not complete until its data compatibility,
tests, documentation, and browser behavior are verified.

## 90% release scorecard

This score measures the critical Notion-class workflows Sentinel owns, not the percentage of
every feature Notion has ever shipped. A domain passes at 90% when all P0 workflows and at
least 90% of its P1 acceptance checks are implemented and verified.

| Domain | Score | Release evidence |
| --- | ---: | --- |
| Workspace shell and navigation | 95% | Three-pane shell, hierarchy, quick find, recents/favorites, breadcrumbs, collapsed branches, keyboard and mobile browser behavior |
| Block editor | 94% | Rich text, nested branches, slash/link/mention menus, columns, undo/redo, durable drafts, selection comments, Notion round-trip |
| Databases | 92% | Properties, persisted filters/sorts/calculations, groups, 10 layouts, templates, peeks, formulas, relations and rollups |
| Collaboration and sharing | 91% | Presence/cursors, anchored threads, notifications, access levels, public-link expiry/indexing/revoke, audit fields |
| Notion connection and sync | 90% | OAuth/token fallback, scoped recursive discovery, incremental sync, durable job status, coverage counters, ZIP restore, guarded push |
| Quality and migration safety | 95% | Additive migrations, 0-warning Release build, 732 tests, desktop/390px browser journeys, live SQLite migration |

Current weighted readiness: **93%**.

### Post-gate enhancements

- Re-anchor selection comments automatically after large edits to the surrounding text.
- Upgrade block-level remote cursors to character-level collaborative selections.
- Add webhook-triggered Notion refresh and field-level conflict review.
- Expand safe Notion write-back beyond explicitly pushed page title/block content.
- Add offline editing and higher-scale concurrent-edit performance budgets.
