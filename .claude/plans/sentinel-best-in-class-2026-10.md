# Sentinel: best-in-class plan (approved 2026-10-09)

Grant (2026-10-09): "I want you to do all that you have suggested" + "Generate a plan for all for me
to approve". Source: a feature-by-feature comparison of Notion, Coda (now Superhuman Docs),
Microsoft Loop, Obsidian, Anytype, Joplin, AFFiNE, Tana, Mem and Slite against Sentinel as it is
today. 33 items: 3 known-limitation fixes (K1-K3), 12 headline gaps (1-12), 18 additions (13-30
plus 31-32 numbered as in chat).

**Status: APPROVED by Grant 2026-10-09 ("Approved. Proceed with this plan").** This file is the tracker:
update each item's status as it lands. Every item is verified with `./scripts/verify-release.sh`
before it's marked done, and the matching `docs/*_USER_GUIDE.md` is updated in the same change.

## Ground rules (apply to every item)

- **Local AI only.** All generation runs on Ollama: the server's small model in light mode
  (summaries, short classification, embeddings) or the browser machine's / Mac's local model through
  `BrowserLocalOllamaService` for anything heavy. No external LLM APIs. Article generation stays
  Mac-app-only.
- **Reuse before building**: the semantic search index (`SemanticSearch`, `embeddinggemma`, hybrid
  keyword + meaning), the collaboration notification bell, the Automation engine (schedule
  triggers, `ai.agent`, workflow buttons), QuestPDF, MailKit, PdfPig, Markdig, the governed-write
  "Confirm / Decline" proposal pattern.
- Known traps from memory: no inline `<script>` (CSP), `@bind:event="oninput"` for buttons gated on
  input, SQLite can't compare/sort `DateTimeOffset` (use Unix-seconds columns or filter in memory),
  string component parameters need `@`, scoped CSS doesn't reach JS-created elements.
- Each item: unit tests for the service, a browser test (`OverwatchGridScriptBrowserTests`-style
  harness or the local AllowAnonymous wrapper technique) for anything with editor JS, and a
  migration only when a table/column is genuinely needed.

## Phase 1 - Quick wins (small, independent; do first)

| # | Feature | Approach | Data | Done when |
|---|---|---|---|---|
| K1 | Person property = real users | Picker over GWS users (+ departments); stored as usernames; "My work" and filters use it. Existing free-text values kept, matched to a user where the name is exact | value format change, no new table | Picking a teammate shows avatar/name; "My work" lists rows assigned to me |
| K2 | Files property with real upload | Upload into the media library, value = list of asset ids; download/preview in grid and row page | value format | Attach, preview, remove a file from a row |
| K3 | Board grouped by Status | Board view offers Status properties; columns follow the status groups' option order | none | Board on Status works incl. drag between columns |
| 1 | Doc verification with expiry + stale-docs report | Page-level owner, "verified until" date, verify/unverify; bell reminder to the owner when it lapses (daily sweep); "Needs review" list (lapsed, never-verified-but-old, never read - see 22); verified pages boosted in search/Q&A ranking | new columns on WikiPage (OwnerUsername, VerifiedBy, VerifiedAt, VerifiedUntil) | Verify a page, see badge; lapse it, owner gets bell + it appears in Needs review; verified page ranks above an equal unverified one |
| 4 | Daily notes | "Today" button + Cmd-Shift-D: opens/creates `Daily/YYYY-MM-DD` from a chosen template; prev/next day arrows; calendar picker; Quick Notes and Mac captures can target today's note | settings row for template id | Today opens/creates once per day; navigation works |
| 6 | Buttons that act + simple database automations | Page Button block can run an Automation workflow (with confirm) as well as open a URL; database "Automations" panel: when a row is added / property changes to X -> set property, notify, run workflow (thin UI over existing `database.rowChangedTrigger`) | button block settings | Button runs a workflow; a "Status = Done -> set Completed date" rule fires |
| 7 | AI builds the structure | "Describe what you want to track" -> local model proposes a database (properties, options, views, starter rows) shown as a preview -> Confirm creates it (governed-write pattern) | none | Prompt -> preview -> one click creates a working database |
| 13 | Date mentions + reminders | `@` menu gains dates (`@today`, `@next Friday 3pm`); a chip renders relative ("in 2 days"); "Remind me" turns it into a bell notification at that time (minute sweep) | new table PageReminders | Reminder fires once, at the right time, linking to the block |
| 14 | Hover previews of links | Hover a `[[link]]`/mention/linked card for ~400 ms -> popover with the page's first blocks (read-only, permission-checked) | none | Preview shows; respects access |
| 15 | Page aliases | "Also known as" field; `[[` autocomplete and link resolution match aliases | new column Aliases | `[[GW]]` resolves to the "Grant Watson" page |
| 17 | Diagram blocks | `/diagram` block with Mermaid source + live preview, rendered client-side by a vendored Mermaid (served from `self`, no inline script) | block type | Flowchart and sequence diagram render in editor, export and public share |
| 19 | Focus mode + reading stats | Focus toggle hides sidebar/toolbars; status bar shows words, characters, reading time, selection count | none | Toggle works; counts update live |
| 20 | Export to PDF and Word | PDF via QuestPDF from the block model (headings, lists, tables, images, code, callouts); .docx via OpenXML; page + optional sub-pages | none | Exported PDF/DOCX open cleanly with images and tables |

## Phase 2 - AI and knowledge (shares the semantic index and the bell)

| # | Feature | Approach | Data | Done when |
|---|---|---|---|---|
| 2 | Ask the workspace, with citations, for every content user | "Ask" box in Sentinel: retrieve top chunks from the semantic index (permission-filtered), answer with the local model (browser/Mac relay where available, server small model otherwise), cite pages inline; verified pages preferred; "not found" answers say so | log table for questions (feeds 23) | Answers cite real pages the asker can see; never cites pages they can't |
| 3 | Related notes + unlinked mentions | Side panel: semantically related pages (index neighbours), and pages that mention this page's title/aliases without linking, each with "Link it" | none | Related list is relevant; "Link it" converts a mention into a link |
| 21 | Workspace-wide tags | `#tag` inline + page tag field; nested tags (`client/acme`); tag pages list everything tagged; tag filter in search | new table PageTags | Tagging, nested browse and filter work |
| 22 | Page analytics | Record page opens (user, time, debounced); page shows views/viewers; workspace "Most read / never read" | new table PageViews (Unix-seconds) | Counts correct; never-read feeds Needs review |
| 23 | Knowledge gaps | Questions from #2 with no good source (low retrieval score or "not found") grouped by topic -> "Write this page" with the question as the brief | uses #2 log | Unanswered questions appear grouped with a create action |
| 25 | AI field for the whole column | "Fill all empty" runs the AI field's prompt row by row as a background job with progress + cancel, on the local model; review-before-write option | job table | 100 rows fill in background; cancel stops it |
| 26 | Clean up a messy note | Writing-assistant action on a page/selection: restructure into headings, bullets, to-dos without adding facts (same guardrails as the existing assistant) | none | Before/after preview, then apply |
| 27 | Caretaker agents | Automation starter templates + Sentinel tools for `ai.agent`: weekly "what changed" digest, owner nudges for stale pages, orphan pages with suggested links | templates only | Each template runs end to end on a schedule |
| 28 | Meeting notes linked to calendar + CRM | Mac Notes tab can pick a booking/contact; saved note attaches to that booking and shows in the contact's History | link columns on the quick-note save endpoint | A note saved against a booking appears on the contact's history |

## Phase 3 - Suite and capture

| # | Feature | Approach | Data | Done when |
|---|---|---|---|---|
| 5 | Live business blocks | `/` blocks that embed live: a CRM contact or deal card, a support ticket, a BI chart, the content calendar (week), time-tracking totals; each re-renders from its service and respects permissions; public share shows a frozen snapshot or hides it | block types | Each block updates when the underlying record changes |
| 8 | Web clipper | Browser extension (Chrome/Edge/Safari via WebExtension) using a per-user clip token: clip page (readable article), selection, or link; picks target page/today's note; endpoint stores page + source URL | ClipTokens table | Clip from a real site lands as a clean page |
| 29 | Email into Sentinel | Dedicated mailbox (IMAP via MailKit, polled) or forward address; sender must be a known user; becomes a page with attachments under "Inbox"; subject tags (`#tag`) applied | settings + processed-message log | Forwarded email arrives as a page with attachments |
| 30 | Publish a page tree as a mini-site | Extends public share: share a page "with sub-pages" -> sidebar navigation, search, custom title; same password/expiry/index options | share flag | Visitor navigates the published tree; unpublished pages stay hidden |
| 31 | More powerful formulas | Formula engine: lists (`map`, `filter`, `join`, `first`, `length`), `let`, relation traversal (`prop("Client").prop("Email")`), date math; backwards compatible | none | New functions work; every existing formula unchanged (test corpus) |
| 32 | Public forms from a database | Form view gets "Share publicly": conditional questions (show if X), required fields, Turnstile, thank-you message; submissions become rows | form settings | Anonymous submission creates a row; conditions hide/show questions |
| 24 | Suggested edits | "Suggest" mode: edits recorded as proposals (insert/delete/replace per block) shown inline; owner accepts/rejects each or all | new table PageSuggestions | Suggest, review, accept partially |
| 16 | Side-by-side panes | Open a page in a second pane (Shift-click / "Open to the side"); each pane has its own editor and autosave | none | Two pages edited side by side |
| 18 | Presentation mode | Present button: slides split at H1/H2 (or dividers), keyboard navigation, full screen, speaker view optional | none | A page presents as slides and exits cleanly |

## Phase 4 - Big bets (each its own sub-plan before code)

| # | Feature | Approach (to confirm in a sub-plan) | Risk |
|---|---|---|---|
| 9 | Real-time co-editing | CRDT per page (Yjs in the editor JS, server relays updates over the existing circuit/SignalR and persists merged state; awareness = cursors). Replaces the conflict banner for live sessions | Highest: touches the editor core, autosave, versions, synced blocks; needs a staged rollout behind a flag |
| 10 | Whiteboard mixing documents | New "Canvas" page type: infinite canvas (vendored open-source canvas lib), page/block cards, shapes, connectors, sticky notes; cards open/edit the real pages | Large UI; library choice + CSP |
| 11 | Typed pages ("supertags") | Page types with attributes; tag any page with a type -> it gains fields; type pages list all members as a database-style view; workspace-wide queries | Model change across pages/rows; migration of existing rows optional |
| 12 | Offline | Mac app first: read-only offline copies of chosen pages + queued Quick Notes; web offline (service worker + local store) only if needed afterwards | Sync/conflict design; CSP/service-worker constraints |

## Decisions needed from Grant before the affected items start

1. **Email-in (29):** a dedicated mailbox to poll (which provider/address?) or skip.
2. **Web clipper (8):** OK to build a browser extension loaded unpacked (no store publishing), or
   should it go through the Chrome Web Store?
3. **Real-time co-editing (9):** worth the risk now, or after everything else?
4. **Order:** phases as listed, or pull specific items forward?

## Out of scope (with reasons)

Plugin ecosystem (single owner), guest accounts (client portal covers outsiders), a Sentinel write
API (read-only key is a deliberate line), end-to-end encryption (breaks server search/AI),
server-side OCR of every image (droplet capacity - possible later in the Mac app), external model
choice (local-only rule).

## Status

| Phase | Items | Status |
| --- | --- | --- |
| 1 | K1-K3, 1, 4, 6, 7, 13, 14, 15, 17, 19, 20 | in progress |
| 2 | 2, 3, 21, 22, 23, 25, 26, 27, 28 | not started |
| 3 | 5, 8, 29, 30, 31, 32, 24, 16, 18 | not started |
| 4 | 9, 10, 11, 12 | not started (sub-plans first) |
