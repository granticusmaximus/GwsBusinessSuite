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
3. **Editor parity — in progress.** Rich-text selection now round-trips bold, italic,
   strikethrough, code, links, safe text colors, and safe highlight colors through the editor,
   server renderer, Notion import, and Notion write-back. Bounded undo/redo history persists
   across page reopen and Blazor reconnect for the browser session. Nested blocks now behave
   as hierarchy-safe branches: split/insert, indent/outdent, duplicate, peer movement,
   drag/reorder, delete, and undo preserve every contiguous descendant while retaining the
   migration-safe flat `IndentLevel` representation. Slash commands, page links, and
   person/date/database-row mentions now share a grouped, descriptive, accessible listbox
   with active-option semantics, Arrow/Home/End navigation, Enter/Tab selection, Escape
   dismissal, responsive positioning, and stale async-result protection. Remaining slices
   cover column movement, selection-aware comments, and durable cross-session drafts.
4. **Database parity.** Unify inline/full-page databases, property menus, filters, sorts,
   groups, layouts, templates, row peeks, calculations, and relation/rollup editing.
5. **Collaboration and sharing.** Presence, cursors, granular comments, notifications,
   permissions, guests, public shares, and audit history.
6. **Sync completeness and observability.** Incremental counters, coverage diagnostics,
   retry/resume, connection webhooks, conflict review, and broader safe write-back.
7. **Quality gate.** Accessibility, responsive and desktop WebView checks, performance budgets,
   migration rehearsal, full tests, and browser journeys.

Each stage is independently shippable. A stage is not complete until its data compatibility,
tests, documentation, and browser behavior are verified.
