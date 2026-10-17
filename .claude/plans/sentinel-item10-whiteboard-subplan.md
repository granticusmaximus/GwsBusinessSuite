# Sentinel item 10: Whiteboard (Canvas pages), sub-plan - BUILT 2026-10-16

**Built 2026-10-16** (Grant: "complete both"). One deliberate deviation: no `PageKind` column - the board is a `canvas` block and a page whose only block is a canvas is a whiteboard page. See the master plan's progress log for what shipped and what was left for later.

Parent: `sentinel-best-in-class-2026-10.md`, Phase 4 item 10. This sub-plan has to be reviewed
before any code is written, as that plan requires. Drafted 2026-10-16.

## Goal

A new "Canvas" page kind: an infinite board with sticky notes, text, shapes, connectors, freehand
pen, and cards for **real** Sentinel pages and database rows. A card shows a live preview, opens the
page in the side pane (item 16), and stays linked when the page is renamed.

## Key decisions (recommendation first)

1. **Rendering library: build it ourselves on SVG with plain JS (recommended).**
   - Excalidraw (MIT) needs React plus a multi-MB ESM bundle and its own fonts. Its data model would
     also fight page cards that point at live records.
   - tldraw is ruled out: its licence needs a key or shows a watermark in production.
   - Konva (MIT, ~150 KB) is the fallback if hand-written hit-testing becomes a time sink.
   - Plain SVG gives a small file, no new dependency and no CSP changes, since everything is served
     from `self` like `wikiMapView.js` and `wiki-page-graph.js`. SVG text also stays selectable and
     easy to export.
2. **Storage: reuse the page, don't add a new table.**
   - New column `WikiPage.PageKind` (null = document, `"canvas"`); migration `AddPageKind`.
   - The board lives in `BlocksJson` as one block of type `canvas` with `props.doc` =
     `{ v:1, items:[...], view:{x,y,zoom} }`.
   - Because the board is a normal page, these all come for free: autosave, ContentVersion conflict
     detection, revisions/history, trash, permissions, sharing, sub-pages, backlinks and search.
3. **Cards reference by id** (`{kind:"page"|"row", id}`). They render through the existing
   permission-checked preview builder (`SentinelPagePreviewBuilder`, item 14). A card the viewer
   can't see shows as "Restricted"; a page in the trash shows as "Deleted page".

## Item model (canvas doc v1)

`sticky` (text, colour), `text`, `shape` (rect/ellipse/diamond, label), `connector` (from/to item
id + side, or a free point; arrowheads; label), `pen` (simplified point list), `card` (page/row ref),
`image` (in-app media id only, the same rule as export), `frame` (named group used as a slide/area).
Every item has id, x, y, w, h, z and an optional colour. Limits: 2,000 items and 1.5 MB per doc,
checked on the server at save.

## Phases (each verified with `./scripts/verify-release.sh`)

| # | Unit | Done when |
|---|---|---|
| A | Domain + server: PageKind column/migration, `WikiBlockTypes.Canvas`, `SentinelCanvasDoc` (Application: parse/validate/limits, text extraction, page/row reference list), save-path validation in `WikiService.SavePageAsync`, "New canvas" in the sidebar/`+` menu | A canvas page is created, saved, reloaded and revisioned; bad or oversized docs are rejected (unit + service tests) |
| B | Editor JS `sentinelCanvas.js`: pan/zoom (wheel, pinch, space-drag), select/marquee/move/resize, sticky/text/shape, undo/redo, copy/paste, keyboard shortcuts, autosave through the existing debounce/ContentVersion path | Draw, edit and reload with the board unchanged (Playwright) |
| C | Connectors that follow their items, pen, frames, alignment snapping | Moving a shape re-routes its connectors (Playwright) |
| D | Page/row cards: drag from the sidebar or `[[` search, live preview, open in the side pane, Restricted/Deleted states, backlinks from canvas references | A card updates after its page is renamed or edited; an Author without access sees Restricted (service + Playwright) |
| E | Integration: search/semantic index and Ask read the canvas text; export (PDF = SVG snapshot rendered to an image plus a text appendix, DOCX = text outline, Markdown = outline); read-only render on public share; presentation mode uses frames as slides; history/restore preview | Each surface tested; the public share leaks no restricted card |
| F | Docs: `docs/SENTINEL_USER_GUIDE.md` canvas section, master plan progress log | Guide updated |

## Out of scope for v1

Real-time multi-user editing (that waits for item 9; v1 uses the same conflict banner as documents),
embedding whole editable documents inside the canvas (cards open the side pane instead), and
importing Excalidraw/Miro files (possible later from the JSON format).

## Risks

- Hit-testing and performance with many items. Mitigations: render only what's on screen past 500
  items, and keep pen paths simplified.
- Tools that assume every page is a document: the indexer, exporters, AI page actions, suggested
  edits, the duplicate/template paths and synced blocks. Sweep for `BlocksJson` readers in Phase A
  and either handle or gate each one by `PageKind` (suggested edits and the AI cleanup action are
  disabled on canvases).
- The Mac app uses a WebView, so test trackpad pinch on MacCatalyst.

## Questions for Grant

1. Is the build-it-on-SVG approach OK, or do you prefer Excalidraw's look and feel, accepting a
   React bundle?
2. Should a canvas be allowed as a sub-page and as a database row page, or only as a top-level and
   sub-page?
