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
- **A5 (incremental sync progress) — not started.**
- **OAuth connection foundation — done.** A Notion public connection can now authorize from
  Sentinel without sharing a Notion password. Access and refresh tokens are encrypted, OAuth
  state is short-lived and bound to the signed-in admin, token rotation and revoke/disconnect
  are supported, and manual tokens remain an advanced fallback. Server deployment still needs
  the Notion client id, client secret, and exact callback URI.
- **Track B (visual and interaction parity) — not started.**

## Track A — Connector and sync fidelity

1. Property type fidelity.
2. Block type fidelity.
3. Relation value resolution.
4. Browsable selective-import picker.
5. Incremental progress reporting for large workspaces.

## Track B — Visual and interaction parity

1. Text and background color.
2. Sidebar drag/reparent and row actions.
3. Keyboard navigation in slash, mention, and wiki-link menus.
4. Real ancestor-chain breadcrumbs.
5. Hover affordances and desktop sidebar collapse.
6. Inline comment highlighting (deferred to a dedicated slice).
7. Unified Command/Ctrl+K over Sentinel content.

## Scope

Sentinel remains a clean-room Notion-class workspace. Functional and interaction parity is
implemented within Sentinel's warm stone/amber identity; Notion source, trademarks, logos, and
proprietary assets are not copied.

Each item is one shippable slice. Update this status, `docs/WIKI_NOTION_CLONE.md`, and
`docs/ROADMAP.md` after each slice. UI-visible slices require a browser exercise in addition to
the clean solution build and full test suite.

The ground-up rebuild sequence and the API's honest synchronization boundary now live in
`docs/SENTINEL_REBUILD.md`.
