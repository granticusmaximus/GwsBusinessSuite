# Technology-forward UX (planned 26-10-26)

Source: the 17 improvement areas proposed in the 26-10-26 session, after admin shell redesign R4-R5b
shipped. Grant said "Proceed" on the list. This file orders them into phases; each phase is a
finished, verified unit (tests, live check at desktop/1200px/phone, user guides,
`./scripts/verify-release.sh`) that leaves the app shippable, since Grant pushes at any point.

**Relationship to admin-shell-redesign-2026-10.md:** that plan's R5c (notification inbox) stays
there and goes first. Its R6 (keyboard) is folded into T1 below, and its R7 (Mac app) into T11.
Standing constraints are the same: `@container gws-main` breakpoints, the 6 `@media` widths, no
inline scripts (CSP), dd-MM-yy / 24h, zero warnings, status colors via `StatusTones`, orange only
for primary actions.

## Order

| Phase | Areas (session numbering) | Why this position |
| --- | --- | --- |
| T1 Command palette 2.0 | 1 command-first, 8 global record search | Biggest "power tool" change; R6 already planned |
| T2 Hover previews | 14 | Small, reuses RelationshipChip and the inspectors |
| T3 Table power | 10 inline edit, 11 bulk bar | Builds on R3 lists, R5 pills and undo toasts |
| T4 Live presence | 3 status strip, 7 live rows and presence | Needs the notifier pattern the bell already uses |
| T5 Density and charts | 2 data density, 15 live charts | Shared chart theme first, then sparklines |
| T6 SentinelGPT in context | 4 | Inspector "Ask", AI drafts shown as diffs |
| T7 Dashboard widgets | 9 | Uses T5's charts and the per-user preference store |
| T8 Activity and transparency | 12 audit timeline, 17 raw view/IDs/trigger trail | One cross-app event feed |
| T9 Motion and themes | 5 motion, 6 terminal theme | Polish once the surfaces exist |
| T10 Empty states and onboarding | 13 | Per-workspace checklists, actionable empties |
| T11 Mac client | 16 (and shell R7) | Native sidebar, hotkey ⌘K, actionable notifications |

## T1. Command palette 2.0 (areas 1, 8; replaces shell R6)

- ⌘K searches records as well as pages: contacts, tickets, invoices, articles, CMS pages, Sentinel
  pages, through the existing `/admin/api/suite-search` endpoint. Results grouped by type, match
  highlighting, type filters (`t:ticket`, `t:invoice`, `is:overdue`), recent items when empty.
- A preview pane for the highlighted result (status pill, key facts), from the same summaries the
  inspectors use.
- An action registry beside the page list: "new invoice for <contact>", "open ticket <subject>",
  "trash page <title>"; actions with an undo use ToastState.
- Keys in app.js (CSP): `g` + letter jumps to a workspace, `j`/`k` move through list rows,
  `Enter` opens, `?` opens a shortcut sheet. Off while typing in inputs and editors.
- Tests: action registry parsing and filters (unit); Playwright: open, filter, run an action.

**Done 26-10-26.** What shipped:
- Server: `SuiteSearchQuery.Parse` (`t:`/`type:` + aliases such as ticket(s)/support, invoice(s)/
  billing, contact(s)/crm, deal(s), page(s)/cms, post(s)/article(s), workflow(s)/automation,
  sentinel/note(s), offer(s)/affiliate; unknown filters stay in the text) and
  `SuiteSearchCategories`. With a filter only that kind runs, 12 per kind instead of 4, and no text
  lists the newest (sorted in memory: SQLite can't order DateTimeOffset). Results deep-link:
  contacts to their page, tickets to `/admin/support?ticket=`, invoices to `/admin/billing?invoice=`
  (the inspector), articles to their editor; deals still open CRM (no page of their own). Untitled
  Sentinel pages read "Untitled".
- **Semantic budget:** the hybrid (embedding) part of suite search queues behind other local Ollama
  work - a SentinelGPT generation held the slot for 9 s in testing, so ⌘K waited on it. It now
  gets 1.5 s (`SuiteSearchService.SemanticBudget`), then the palette goes on with keyword matches.
- `/admin/api/shortcuts` returns the person's pins and recents (R1b) for the empty palette.
- Palette (app.js): sections Pinned and Recent (empty search), Actions, Pages, then records grouped
  by kind with the match highlighted (`<mark>`, built with DOM nodes, never innerHTML of data);
  "Searching records…" while the record search is out instead of "no matches"; a footer hint for
  `t:`. Actions: New contact/invoice/ticket/page/post/workflow (each shown only when its page is in
  the person's sidebar), Switch theme, Keyboard shortcuts, Sign out. Create actions open
  `?new=1`, which each page handles on its first interactive render after loading: CRM opens quick
  create, Billing resets and Support keeps their forms and focus the first field one render later
  (FocusOnNavigate moves focus to the h1 first), Pages opens the new-page dialog, Automation the
  create dialog, Posts creates the draft and replaces the address so Back doesn't make another.
- Keys (app.js, CSP-safe): `g` + h/c/r/k/i/g/p jumps to a workspace's first page, `j`/`k` move a
  row cursor through the page's list and Enter opens the row, `/` focuses the page's search, `?`
  opens the shortcut sheet (static markup in MainLayout). Off while typing, with a modifier held,
  while a dialog is open, and on Sentinel, Overwatch and the page editor.
- **Fixed along the way:** an enhanced navigation finishing while the palette was open reset its
  markup (closed it and cleared the search mid-word) - the palette and sheet are now
  `data-permanent`, and opening a result closes the palette itself; FocusOnNavigate could take
  focus out of the open palette, so focus is kept inside it and the sheet (they're modal);
  Chrome's blue clear button in the search box went.
- The preview pane moved to T2 (hover previews), which builds the per-record summaries both need.
- Tests: `SuiteSearchServiceTests` (11: deep links, filters, newest-first listing, the parser, the
  sentinel alias, and a stalled semantic search not holding up keyword results).
- Checked on a throwaway instance: the empty palette's sections; "dana" (searching state,
  highlight, deep link, palette closes after the pick); `t:ticket` (only tickets, opens the ticket
  selected in Support); `t:invoice octo` (opens the invoice inspector); every create action (focus
  in the form, the dialogs, the new post, Back); theme; the sheet; `?`, `j j` + Enter, `/`, typing
  `jk` staying in the input, `g r`, `g h`; no console errors.

## T1b. SentinelGPT in ⌘K (added 26-10-26 at Grant's request: "introduce some Ollama functionality" in SuiteSearch)

**Done 26-10-26.**
- Suite search already used Ollama for embeddings (semantic matches for contacts, deals, CMS
  pages; budgeted to 1.5 s in T1). T1b adds plain-language search: `ISuiteSearchAssistant` /
  `SuiteSearchAssistantService` (Application) sends the request to the SentinelGPT model (site
  override, else `sentinelgpt`) with one native tool, `find_records` (kind, text, status, overdue,
  within_days, priority, contact, summary), and runs the call with `ISuiteSearchService.FindAsync`
  against the real records - the model chooses filters, never results.
- `SuiteSearchRequestRules`: `LooksLikeRequest` (3+ words or a question, no `t:` filter),
  `FromToolArguments` (unknown kind or bad JSON = no search; strings trimmed and capped;
  within_days 1-365), `NormalizeStatus` (each kind's real statuses plus everyday words - unpaid ->
  Sent, unresolved -> Open, live -> Published; an invented status is dropped rather than hiding
  every record), `NormalizePriority`, and a backstop from the request's own words: `FillFromQuestion`
  (a status or priority word the model left out) and `OwnerFromQuestion` ("for/from <Name>", used
  only if such a contact exists, else the search runs without it). For tickets, invoices and deals
  the text also matches the contact's name (models put "Dana" in text for "invoices for Dana").
  Seen live before these: "unpaid invoices for Dana" found nothing (name in text), "urgent
  tickets" read as overdue (no priority option), "open tickets this week" lost "open".
- `/admin/api/suite-search/ask` (15 s budget, 204 = no answer). The palette sends plain-language
  requests there 700 ms after typing stops, below its keyword search: a "SentinelGPT" group at the
  top says "Turning your request into a search…", then shows "SentinelGPT · <summary>" and the
  records, or a line saying it found nothing; the selection never jumps. An "Ask SentinelGPT"
  action (2+ words) closes the palette and asks in the assistant panel (the island registers a
  .NET reference with `gwsAssistant.register`; `AskFromPaletteAsync` opens and asks; a stale
  reference falls back to opening the panel). Each request's tool arguments are logged
  ("⌘K request ... became ...") for tuning.
- Tests: `SuiteSearchAssistantTests` (20: argument checks, request detection, status and priority
  mapping, the question backstop, FindAsync's overdue/contact/status/recency/follow-up/priority
  filters, the assistant with a scripted Ollama including no tool call, failure, the override
  model, short lookups never reaching the model, and the owner-name fallback).
- Checked live against this Mac's Ollama (`sentinelgpt`): five requests each answered in about
  1 s with the right records after the fixes above; the palette group, steady selection, Ask
  SentinelGPT opening the panel with the question, no console errors.
- Production note: the droplet runs a lighter model (light server-AI mode); tool calls there may
  be less precise, which the backstop and status mapping are for. Watch the log line.

## T2. Hover previews (area 14)

- A shared `RecordPreview` popover on RelationshipChip, ticket/invoice links and ⌘K results:
  status pill, three key facts, two quick actions. Hover (300ms delay) and focus open it; Escape
  closes; touch opens the inspector instead.
- One preview provider per record type, reusing the inspector queries (factory DbContext per call,
  since layout islands share the circuit).

**Done 26-10-26.**
- `RecordPreviewService` (Web/Services; factory DbContext per call) summarises a contact (status,
  company, email, follow-up "due dd-MM-yy", open tickets and unpaid invoices; Open contact, Email),
  a ticket (status, contact, priority, opened, due or "overdue since"; Open ticket, Contact) or an
  invoice (status, client, total in its currency from the line items, due or "overdue since",
  sent/paid; Open invoice, Client), with the status pill's tone from `StatusTones`.
  `/admin/api/preview/{kind}/{id}` (AdminOnly).
- app.js: anything marked `data-preview="kind:id"` (RelationshipChip, CRM contact names, Billing
  invoice titles, Support ticket subjects) shows the card after 300 ms of hover or on keyboard
  focus; it stays while the pointer is over it, hides 200 ms after leaving or on Escape, flips
  above the trigger near the bottom edge, and never shows on touch screens or under the palette.
  One request per record per page (cache cleared on navigation). The card container is static
  markup (`data-permanent`, `aria-hidden` - supplementary; the trigger opens the record).
- ⌘K preview pane (moved here from T1): results whose address is a contact, ticket or invoice
  carry `data-palette-preview`; from 1024px the dialog widens to 920px with the highlighted
  record's preview beside the list, following arrow keys, typing and late results.
- **Fixed along the way:** `/admin/api/suite-search` returned CRM contacts, deals, tickets,
  invoices, workflows and affiliate offers to any signed-in user, including Author and Contributor
  accounts who can't open those pages. `SuiteSearchVisibility.For` now gives non-admins only
  Sentinel results (already access-checked), site pages and posts; the ask and preview endpoints
  are AdminOnly.
- Tests: `RecordPreviewTests` (5: each kind's facts, overdue wording, the invoice total, unknown
  kind/record, and the visibility rule).
- Checked live: invoice and contact cards by hover (stay over the card, hide after leaving), a
  ticket card by keyboard focus and Escape, the palette pane widening and following the selection,
  no card on a touch phone, no console errors.

## T3. Table power (areas 10, 11)

- Inline edit for status, priority, due date and assignee cells on CRM, Billing, Support:
  click or Enter to edit, optimistic save, undo toast, a validation error puts the old value back.
- A bulk bar when rows are selected (checkbox column, shift-click ranges, `x` to select with T1's
  keys): Assign, Status, Export CSV, Trash, each with undo where the action has one.
- Shared pieces in a `ListSelection` helper so pages don't each reimplement selection.

Built in two units: T3a CRM contacts, T3b Support and Billing.

**T3a done 27-10-26 (CRM contacts).**
- `ListSelection<TKey>` (Web/Components): click toggles, shift-click selects the range from the
  last clicked row in the list's current order (taking that row's state, so it can also clear a
  range), select-all over the rows on show, `Retain` after a reload. `CsvExport.Build`: quotes
  fields with commas/quotes/line breaks, and prefixes =, +, - and @ with ' so a spreadsheet never
  runs a cell as a formula.
- `ICrmService.SetContactStatusAsync` (status only, checked against `ContactStatuses.All`), so a
  list edit can't overwrite a field changed on the contact's page meanwhile.
  `AutomationWorkflowTests.FakeCrmService` implements it.
- CRM contacts: a checkbox column (with select-all), checked rows tinted; a bulk bar above the list
  (count, Set status with Undo restoring each contact's own status, Export CSV with ISO dates via
  `gwsDownload.text`, Move to Trash with Undo restoring them, Clear). Bulk actions reach only
  checked rows that are on show, so a search hiding a checked contact can't trash it unseen. The
  Status cell is a select dressed as the status pill (changes at once, Undo, a refusal reverts);
  44px on touch. `x` checks the row under the j/k cursor (shift+x extends the range).
- **Fixed along the way:** CRM's and Billing's search-and-filter row (R3b) pushed the status filter
  off a 390px screen - the content panel scrolled sideways (right edge 521px). The rows wrap now
  (`gws-list-filters`). The responsive audit didn't catch it because it measures the document,
  not the content panel, which scrolls on its own; a sweep of all 50 admin routes found five more
  pages like it (Menus, Builder Reference, Government Intelligence, Growth, Pages > All) - see the
  next unit.
- Tests: `TablePowerTests` (5: toggle and range, range direction and anchor state, select-all and
  retain, CSV quoting and formula guard, the status-only update and its refusals).
- Checked live: shift-click range (3 selected), bulk status (only the 2 that differed) and Undo,
  CSV download, Clear, inline status with the pill's tone and Undo, bulk trash and Undo, `j` `x`,
  phone select 44px and no overflow, no console errors.

**T3b done 27-10-26 (Support and Billing).**
- `ISupportTicketService.ApplyTriageAsync` (one status, priority or assignee change across tickets
  in one save; all or nothing; returns snapshots of what changed) and `RestoreTriageAsync` (Undo).
  Undo restores a snapshot rather than setting the old status again: that would restart the SLA
  clock, so undoing an accidental Resolve would hand the ticket a fresh deadline and hide a breach.
  `SetStatusAsync` and list edits share one `ApplyStatus` rule.
- Support list: checkbox column, bulk bar (Status…, Priority…, Assign to me, Unassign, Export
  CSV, Clear), Priority and Status cells as pill selects, an optional Assignee column with a
  select (everyone already holding a ticket, plus you). A refused edit bumps an `@key` so the
  select re-draws: Blazor otherwise leaves the refused choice showing, since the value it rendered
  didn't change. **Found live in T3a's CRM status** (trash a contact in one tab, change its status
  in another: the toast said no, the pill still showed the new status); fixed the same way, and
  `TablePowerTests` now fails any inline control without an `@key`.
- `IBillingService.SetDraftDueDateAsync` (Draft only; Stripe holds a sent invoice's date) and
  `SetAutoRemindAsync` (Draft and Sent only; returns the ids it changed so Undo flips just those).
- Billing list: checkbox column, bulk bar (Auto-remind on/off with Undo, Export CSV with plain
  totals and ISO dates, Delete N drafts behind a confirm, Clear), a bell on auto-reminding
  invoices, and a draft's Due cell as a date box (read as the UTC date it's stored as) with Undo.
  Invoice status isn't editable in the list - Stripe moves Sent to Paid - and bulk Send, Void and
  Remind are left out on purpose: they reach the client or Stripe and can't be undone.
- Tests: `SupportTicketServiceTests` +3 (bulk change skipping tickets already as asked, assignee
  trim and unassign; nothing changes on an unknown value or ticket; Undo of a Resolve keeps the
  original, already-passed SLA deadlines), `BillingServiceTests` +2 (draft due date stored as UTC
  midnight and refused once sent; auto-remind skips void invoices and returns only what changed).
- Checked live: Support shift-click (2), bulk status (1 of 2 changed, toast names it) and Undo,
  bulk priority and Undo, Assign to me and Undo, CSV, inline status with the pill's tone and Undo;
  Billing select-all, auto-remind on (2 of 3; the paid one skipped) and Undo, CSV, the delete
  confirm's text, inline due date and Undo; phone: no panel overflow, inline controls 44px, bulk
  bar within the screen; no console errors. User guides: CRM (Billing section) and Support.

## T4. Live presence (areas 3, 7)

- A slim status strip in the shell: circuit connection, background jobs running, SentinelGPT
  activity, app version and deploy time. Click opens Mission Control on the dashboard.
- Live list rows: when a notifier reports a change to a visible record (ticket reply, invoice paid),
  the row updates in place with a 1.5s highlight instead of needing a refresh.
- Presence: avatars of other signed-in staff viewing the same record (community users exist), via a
  scoped presence service over the existing hub pattern.

Built in three units: T4a status strip, T4b live list rows, T4c presence.

**T4a done 27-10-26 (status strip).**
- `MainLayout`: a `<footer class="gws-status-strip" data-permanent>` after the content, sticky to
  the window's bottom, hidden below 768px (the phone bar's place; `--gws-status-strip-height`
  drops to 0 there) and in print. The toast now floats above it. One link to `/admin`, where the
  Mission Control cards live.
- `ShellStatusService` (Web/Services, DbContext per call) behind `GET /admin/api/shell-status`
  (AdminOnly, public-read rate limit): automation executions Running with a heartbeat inside
  `AutomationExecutionService.OrphanThresholdSeconds` (now public; a quieter one crashed),
  `OllamaWorkloadScheduler.Activity` (holding the model, and how many wait), and a `BuildStamp`:
  the web assembly's write time and the commit the SDK appends to the informational version. The
  Docker image is built without .git, so production shows the time alone - passing the sha as a
  build arg in deploy.yml would add it, left alone since it touches the deploy.
- app.js `bindStatusStripOnce`: fetches now, every 30 s and when the tab shows again (none while
  hidden; a sign-in redirect or 401/403 stops it); connection state from Blazor's
  `components-reconnect-state-changed` on the reconnect dialog, and connection-check.js now
  dispatches `gws:connection` when the circuit can't start, so the strip says Offline too.
  Dates dd-MM-yy HH:mm in the browser's time.
- `data-permanent` keeps the strip's children (what app.js wrote) across enhanced navigation, but
  Blazor re-syncs the element's own attributes - checked: a marker attribute on the strip and on
  the palette layer is dropped, the strip's text isn't. Keep state in children, not on the root.
- Tests: `ShellStatusTests` (live vs crashed/finished/waiting executions, scheduler activity
  through a hand-over, commit parsing x5).
- Checked live: API JSON, strip text (Live, No jobs running, SentinelGPT busy · 1 queued from the
  fresh instance's own background AI work, build time and commit), pinned at the bottom while
  scrolling, toast above it, click opens the dashboard, kept across enhanced navigation, hidden on
  a phone, server stopped -> amber Reconnecting….
- **Fixed along the way (enhanced navigation, found checking the strip):** Blazor's merge keeps a
  `data-permanent` element's children but re-syncs its own attributes, and re-syncs `<html>`,
  `<body>` and the layout's classes. So (1) the ⌘K palette and shortcut sheet still closed when a
  navigation finished (T1's fix put `data-permanent` on the element whose `hidden` app.js toggles)
  - the palette, sheet and preview card now sit inside `data-permanent` wrappers; (2) app.js's
  after-navigation setup listened for a `blazor:enhancedload` DOM event Blazor never raises, so it
  never re-ran: a collapsed sidebar re-expanded and light theme went back to dark on every page
  change (both older than this plan), and the j/k cursor and preview cache went stale. It now
  uses `Blazor.addEventListener('enhancedload', ...)`, re-applies the stored theme, and restores
  the palette's scroll lock. Checked live before and after each; `EnhancedNavigationGuardTests`
  fails a permanent element carrying `hidden` or the dead event name.

## T5. Density and charts (areas 2, 15)

- One chart theme (Chart.js is already vendored) from the status and text tokens, light and dark,
  with hover crosshairs and a previous-period comparison line.
- Sparklines in KPI tiles and in list rows where a trend exists (deal value, visitors, campaign
  sends). "Updated 12s ago" indicators on live data.
- Compact as the default suggestion for mouse users (first visit offers it; the switch stays).

## T6. SentinelGPT in context (area 4)

- "Ask about this" in every RecordInspector: the record is already the assistant's context; the
  inspector gets an inline prompt and answer area instead of opening the side panel.
- AI drafts (Support replies, CRM follow-up emails, article summaries) shown as a diff you accept,
  edit or discard; nothing is sent without an explicit accept.
- Light server-AI mode applies: droplet runs llama3.2, so drafts stay short; the Mac app uses local
  Ollama.

## T7. Dashboard widgets (area 9)

- Home becomes a grid of widgets (KPIs, queues, Overwatch cameras, World News, Docker health,
  Recent), drag to reorder, resize in two sizes, add/remove from a picker. Layout saved per user in
  `UserPreferences` (add a key with a JSON value and validation).

## T8. Activity and transparency (areas 12, 17)

- One activity page: edits, sign-ins, deploys, automation runs across the suite, filterable by app,
  person and record, linked to the record. Sources: security audit, contact timelines, automation
  executions, deploy info.
- Per record: copy ID, copy link, "view raw" (JSON of the record's view model, admins only),
  and "what triggered this" for records an automation or email-in created.

## T9. Motion and themes (areas 5, 6)

- Motion tokens (120-160ms, one easing), the inspector sliding over a dimmed list, skeleton-to-
  content crossfade, glow focus rings in dark mode; everything off under reduced motion.
- An optional "terminal" theme: cooler neutral base, one electric accent, mono headings on system
  pages, a subtle grid texture on empty states. A third theme choice beside light and dark,
  stored per user.

## T10. Empty states and onboarding (area 13)

- Empty states get a specific first action plus one suggestion from your data ("3 deals have no
  invoice: create one"). A first-run checklist per workspace, dismissible, stored per user.

## T11. Mac client (area 16, shell R7)

- Native sidebar mirrors the workspace rail; ⌘1-7 switch workspaces; a global hotkey opens ⌘K;
  notifications with actions (reply, resolve, snooze); check every R/T phase in WKWebView.

## Open questions for Grant (decide when the phase starts, not now)

- T4 presence: worth it while most use is single-admin? (Cheap once the status strip exists.)
- T9 terminal theme: a third theme, or a variant of dark?
- T6: which drafts first (Support replies are the most frequent)?
