# Boards: a dedicated Kanban / sprint page (27-10-26)

Grant: "add a KanBan/Trello/Sprint board for me for work ... there is some form of a KanBan in
Sentinel ... expand on that functionality and add it as its own page ... do market research on top
KanBan board apps, figure out what the best features are, what users want and build me a
modernistic KanBan page."

## Market research (27-10-26)

Tools looked at: Trello, Linear, Jira, Notion, ClickUp (plus Asana/Monday in comparisons).

What each is loved for:
- **Linear** - speed and a keyboard-first board: arrow keys between cards, shortcuts to change
  status, `C` to create, ⌘K for everything; a triage inbox before the backlog; **cycles** (light
  sprints with auto-rollover of unfinished work). Criticised as opinionated / less customisable.
- **Trello** - the simplest drag and drop; cards with labels, covers, checklists (progress bar),
  due dates, members; Butler rules and buttons; Inbox/Planner for personal prioritising.
  Criticised for not scaling past ~50 cards, weak reporting, no native swimlanes or timeline.
- **Jira** - sprints, backlog, WIP limits, swimlanes, reports. Criticised for complexity
  ("everyone has to become a Jira admin").
- **Notion** - a board is one view of a database (table, calendar, timeline too), with every card
  a page. Best when docs and tasks live together.
- **ClickUp** - WIP limits, custom statuses, swimlanes, many views; criticised as overwhelming.

What users keep asking for: WIP limits (per column and per swimlane), swimlanes, aging/blocked
work made visible, fast keyboard triage, better search, real reporting. Shared complaint: every tool
is either too simple (Trello) or too heavy (Jira) - "almost everyone wishes there was a middle
ground".

Flow metrics the Kanban method relies on: cycle time (start to done), throughput (done per week),
cumulative flow diagram (cards per column over time - bands show where work piles up), and work
item age (how long an unfinished card has been in progress - the leading indicator of stuck work).

Sources: guptadeepak.com/tools/top-10-project-management-tools-2026, eesel.ai/blog/trello-alternatives,
clickup.com (kanban boards, Trello review), linear.app/build, dev.to (Linear keyboard-first),
forum.asana.com (Linear board keyboard triage), gitlab.com issue 7936 (swimlane WIP limits),
jira.atlassian.com GHS-2478 (swimlanes), scrum.org (work item aging), nimblework.com (kanban
metrics), gembaacademy.com (cumulative flow).

## Design: the middle ground

Fast like Linear, simple like Trello, flexible like Notion, honest about flow like a Kanban tool -
without Jira's admin burden. Opinionated defaults, everything optional.

**Built on Sentinel databases, not beside them.** A board is a Sentinel database (WikiDatabase)
with a Status property for columns; every card is a row, so every card is also a page with its own
content, and the same board opens in Sentinel as a table, calendar or timeline. Any Sentinel
database with a Status or Select property can be opened as a board. The engine already has: Status
with To-do / In progress / Complete groups, MoveRowAsync (column + position), UniqueId ("TASK-42"),
CreateDatabaseFromDesignAsync (board templates), row templates, recurring rows, sub-items.

**New, where the engine has nothing:** board settings (WIP limits, card fields, swimlane), a card
status-change log (cycle time, CFD, aging), sprints.

Route: `/admin/boards` (your boards, new board from a template) and `/admin/boards/{databaseId}`
(the board, full width). Nav: Knowledge workspace, beside Sentinel; ⌘K "Boards", "New card".

## Phases

### K1 - The board page
- `/admin/boards`: boards (Sentinel databases with a Status/Select property), card counts, last
  activity; New board from a template: Personal Kanban (Backlog, To do, Doing, Review, Done),
  Sprint board (adds Sprint, Estimate), Bug triage (Triage, Confirmed, Fixing, Verifying, Closed),
  Content pipeline (Idea, Drafting, Editing, Scheduled, Published). Each creates the database with
  Status (grouped), Priority, Labels, Due, Assignee, Estimate, a "TASK-" UniqueId.
- `/admin/boards/{id}`: columns from the Status options in order, column counts; cards show the
  ID, title, label chips in their colours, priority icon, due date (amber soon, red overdue),
  assignee initials, checklist progress (to-do blocks in the card's page), sub-item count, cover.
- Drag and drop within and between columns (MoveRowAsync), keyboard-accessible alternative;
  quick add at the top of each column (type, Enter, keep typing).
- Open in Sentinel; responsive (columns scroll-snap on a phone).

**K1 done 27-10-26.**
- `IKanbanBoardService` (Application/Boards) / `KanbanBoardService` over `IWikiDatabaseService`:
  boards = databases with a Status property (or a Select a Board view groups by); fields found by
  type and name (Priority, Labels/Tags, Due/Deadline, Assignee/Owner or the first Person,
  Estimate/Points, the first UniqueId). Templates create Status (grouped, coloured), Priority,
  Labels, Due, Assignee, Estimate, Sprint (sprint template) and an ID prefixed from the board's
  name ("Website relaunch" -> WEB-1); the Sentinel views become Board (first, so Sentinel opens
  it) and "All cards" - SaveViewAsync keeps a saved view's type, so the default table is replaced,
  not converted. Option colours pass `SafeColor` (hex or a plain name) before reaching a style
  attribute.
- Moves are "before card X" (or last), translated to MoveRowAsync's index among every row in the
  column - sub-items and trashed rows included - so hidden rows can't shift a drop.
- `/admin/boards` (template picker, tiles with open/total and a done bar, ?new=1) and
  `/admin/boards/{id}`: full-height columns that scroll sideways, each column's cards on their own;
  cards with ID, priority bars, title, labels, due (amber <= 2 days, red + edge when overdue, not in
  a Complete column), checklist n/m (non-empty to-do blocks), sub-items, estimate, initials; quick
  add (Enter keeps going, Esc closes); filter; Sentinel button; card click opens the row in
  Sentinel until K2's drawer.
- `wwwroot/js/kanban-board.js` (imported by the page): pointer drag with a floating clone and a
  drop line (Blazor's DOM is never moved), auto-scroll at the edges, the drop's click swallowed;
  touch drags from a grip (touch-action: none) so swipes still scroll; arrows / hjkl focus, [ ]
  move a column, Alt+arrows reorder, Enter opens. Optimistic in .NET, Undo on column moves, a
  failed save reloads and says so.
- Tests: `KanbanBoardServiceTests` (5: template layout and views, quick add order and IDs, moves
  around a hidden sub-item, card details from row and page, any Status database is a board and
  colour sanitising).
- Checked live: create from template, quick add x3, mouse drag across columns (ghost, line,
  target tint, toast) and within one, ] / Undo / arrows, filter, order after reload, card ->
  Sentinel, boards tile, phone (85% columns, no overflow, 40px grip).

### K2 - Cards: drawer and keyboard
- Card drawer: title, every property editable in place, the card's page body (description,
  checklists) with the Sentinel editor, sub-items, created/updated, a link to the full page.
- Keyboard: arrows / hjkl between cards and columns, Enter opens, `c` new card, `[` `]` move a
  card left/right, `1`-`4` priority, `e` rename, `x` select, Esc closes; ? sheet lists them.
- Multi-select + bulk move/label/priority/trash with Undo (T3's ListSelection and toasts).

**K2 done 27-10-26.**
- The drawer is Sentinel's own `SentinelDatabaseRowPage` (side peek: the card's page, history,
  autosave) with a new `FieldsContent` slot holding `KanbanCardFields`: title, Status (moves the card
  to the top of that column), Priority, Labels (toggle chips), Due, Assignee (+ Me), Estimate, any
  other Select (Sprint), sub-items (tick done, open, add), created/updated. `?card={rowId}` opens
  it, so a card has its own link.
- **Fixed in Sentinel along the way:** the row page's autosave resent the property values it opened
  with (`editor.Values` is a full replacement), reverting anything changed since - a table cell,
  a board drag, the drawer's fields. `WikiDatabaseRowEditor.KeepPropertyValues` makes a page-only
  save keep the stored values (and skip re-validating them); the row page always sets it, since it
  never edits properties. Also: wiki-block-editor.js's `OnCursorMoved` call raised an unhandled
  rejection on every cursor move in any host without live cursors (Sentinel's row page too) - the
  try/catch never saw the async failure; it now catches the promise.
- Dates "as written" (`DateTimeOffset.Date`): Sentinel stores a picked date at UTC midnight, which
  local time showed as the day before west of UTC.
- Service: `SetCardsValueAsync` (one property, per-card change function, returns previous values),
  `RestoreCardValuesAsync`, sub-items (list/add/done), trash/restore. `KanbanBoard` carries the
  Priority and Label options.
- Keys (kanban-board.js; off while typing or the drawer is open): `e` open + rename, `c` quick add
  in the card's column, `x` select (⇧ range), `0`-`4` priority with Undo, `?` shortcut card, Esc
  clears the selection; ⇧/⌘/Ctrl-click selects instead of opening (a capture listener, so Blazor
  never sees that click). Bulk bar: Move to, Priority, Add label, Assign to me, Trash - each with Undo.
- Tests: `KanbanBoardServiceTests` +3 (a page-only save keeps a field changed since it opened;
  per-card set, unchanged skipped, restore; sub-items and trash/restore). Sentinel's 170+ database
  tests still pass.
- Checked live: every drawer field, sub-items, status move, rename, autosave then reload keeps
  the fields, `2` / `?` / `c`, ⌘-click selection, bulk move + Undo, label, trash + Undo, deep link,
  no console errors.

### K3 - Flow control
- WIP limits per column (header "3 / 4", amber at the limit, red over it).
- Card aging: days in the current column on each in-progress card, faded dots after N days.
- Blocked: a flag with a reason, a red edge, and a Blocked filter.
- Swimlanes by Priority, Assignee, Label or Sprint (or none), collapsible, with counts.
- Filter bar: search, labels, assignee, priority, due (overdue / this week), "My cards"; saved
  per person (view personalization).

**K3 done 27-10-26.**
- New tables (migration AddKanbanFlow): `WikiDatabaseRowStatusChanges` - written by
  `ApplicationDbContext.RecordStatusChanges` on every save for any Status/Select value a row gains,
  loses or changes, whichever path wrote it (a page-only save writes none) - and
  `KanbanBoardSettings` (one row per board, `KanbanSettings` JSON: WIP limits by option id,
  SwimlaneBy, AgingDays, and K7's Background / Accent / CardSize).
- WIP: a column's "n / limit" pill, amber at it, red over it with a red outline; set from the
  column's ⋯ menu. Counts every card in the column, whatever the filters hide.
- Card age: days since the card last moved into its current column (that column's latest log
  entry; creation for rows from before the log) on in-progress cards; fades in three steps (half,
  1x, 2x the board's AgingDays, default 5) with a warm edge; set in the board settings popover.
  A move resets it on screen at once.
- Blocked: Checkbox "Blocked" + Text "Blocked reason" (templates create them; `SetBlockedAsync`
  adds them to a board without), a red edge and pill with the reason as its tooltip, `b`, and a
  toggle + reason in the drawer.
- Filters: My cards, Blocked, Priority (incl. none), Label, Assignee (incl. unassigned), Due
  (overdue / within 7 days / none), Clear. Saved per person per board through
  `IUserPreferenceService` under `boards:filters:{id}` - `UserPreferenceRules` gained that key
  family (a JSON object up to 2,000 characters).
- Swimlanes by Priority, Assignee or Sprint (shared board setting): a sticky row of column heads,
  then one collapsible lane per value (priority and sprint show every option, assignee shows who
  holds cards, you and Unassigned). Drop targets are found in 2D (kanban-board.js), the drop sends
  its lane, and a card dropped in another lane takes that value (assignee lanes replace the
  assignees); Undo restores both. Sideways keys stay in the lane.
- **Fixed:** JS-invoked handlers get no automatic render. A cross-lane drop saved but kept
  drawing the card in its old lane until the next redraw - `MoveCard` (and `ApplyAsync`'s failure
  path) now call StateHasChanged after reloading. Also the admin bar's "GWS Business Suite" text
  was fixed white and vanished on the light theme; it follows the theme now.
- Tests: `KanbanBoardServiceTests` +6 (settings round trip, limits on columns, refusals and colour
  cleaning; blocking adds fields once and carries the reason; entered-column time from the log and
  from creation; a move then a lane value both stick; the status-change log; filter preference
  rules).
- Checked live: WIP at and over the limit, `b` + reason, filters (and after reload), lanes by
  priority, a cross-lane drag landing with the new priority on screen and after reload, Undo, lanes
  off, ages at all three levels.

### K4 - Sprints
- Sprint planning: backlog beside the sprint; drag in, see the estimate total against capacity.
- Start / complete a sprint; complete moves unfinished cards to the next sprint (rollover), with
  a summary. Sprint goal on the board header; burndown (estimate or card count) in the header.

**K4 done 27-10-26.**
- A sprint is an option of the board's Sprint property (Select "Sprint"/"Cycle"/"Iteration";
  `CreateSprintAsync` adds the property to a board without one - also the settings popover's "Use
  sprints") plus a `KanbanSprint` entry in `KanbanSettings.Sprints`: goal, start/end, state
  (planned / active / done), capacity, and at completion the done cards and points.
- `ListSprintsAsync`: counts, points (estimates when the sprint's cards carry any, else one per
  card) and a burndown - points left at the end of each day from the start to today/end, a card
  dropping out on the day it last reached a Complete column (status-change log; from the start
  for one finished before the log). Scope is the sprint's cards now.
- `StartSprintAsync` (one running at a time; end after start), `UpdateSprintAsync` (goal,
  capacity), `CompleteSprintAsync` (done figures recorded; unfinished cards to another open
  sprint, the backlog, or a new sprint). Settings changes re-read what's stored first.
- `KanbanSprintBar` (own component): the running sprint's name, goal, "Day n of m", progress and a
  burndown sparkline against the ideal line, Plan / Complete sprint; otherwise "No sprint running"
  and the last sprint's result. Planner: pick or make a sprint, goal, capacity, start date and
  1-4 weeks, recent velocity, backlog (unsprinted first, by priority) beside the sprint with
  + / -, a red total over capacity, Start. Complete: done summary and where the rest go.
  Board: a "Sprint n" filter chip shows only the running sprint's cards.
- Tests: `KanbanBoardServiceTests` +4 (create adds the field and refuses a duplicate; one running;
  complete records and rolls over to a new sprint; burndown by finish day). 18 board tests.
- Checked live: plan (goal, capacity 12, four cards = 11 pts, a start three days back), start,
  the bar (Day 4 of 14, 1 / 11 pts, sparkline), the sprint filter, complete with 3 moved to a new
  Sprint 2, the bar afterwards; no console errors.

### K5 - Flow metrics
- Status-change log (new table): every column change on a board card, with time and who.
- Insights panel: cycle time (median, 85th percentile, scatter), throughput per week, cumulative
  flow diagram, aging WIP; per card, its time in each column.

**K5 done 27-10-26.**
- `GetInsightsAsync(databaseId, 30|90|180)` from the status-change log: cycle time per card now
  complete (first entry into an in-progress column - or its creation - to its last entry into a
  complete one), median and 85th percentile (nearest rank), throughput by Monday-start week,
  cumulative flow (each card's column at the end of each day from its last logged move; a card
  with no log sits in its current column from its creation; complete columns are the bottom
  band), work-in-progress age oldest first, and where the log starts (`HistoryStarts`).
- `KanbanInsightsView` (Board / Insights switch, `?view=insights`; opening a card keeps the view):
  period presets, four stat tiles, cumulative flow (legend, direct labels on bands >= 14px, a
  crosshair tooltip listing every column), weekly throughput (<= 24px columns, rounded tops,
  value on the last), cycle-time scatter (8px dots with a surface ring, 24px hit areas, 50% / 85%
  lines, click opens the card), aging bars against the 85% line with a "Past 85%" status pill;
  every chart has a Data table. kanban-insights.js: one tooltip, values first, line keys, all
  text via textContent. Charts keep their drawn size and scroll sideways on a narrow screen.
- Palette: the columns' own colours (colour follows the column). The validator passes them on
  CVD, normal-vision and contrast in both themes; Backlog/Triage grey fails the chroma floor by
  design ("not started"), so bands always carry a legend and direct labels.
- **Razor traps found:** SVG `<text>` is Razor's reserved pseudo-tag (it cut the @code block
  short; errors pointed at the @code members) - labels are written by `SvgText` as encoded
  markup and styled through ::deep; and `M@F(x)` / `column@(` read as email addresses, never
  evaluated - those strings are built in C#.
- Tests: `KanbanBoardServiceTests` +1 (cycle times 2/4/6 days, median 4, p85 6, Monday weeks,
  WIP and age, flow bands bottom-up and a past day's count, 45 days refused). 19 board tests.
- Checked live on a seeded "Support ops" board (16 cards over six weeks): tiles, flow with
  tooltip and crosshair, bars, dots and their tooltip, dot opens the card in the Insights view,
  90 days, phone without overflow, no console errors.

### K6 - Automation and polish
- Rules: when a card enters a column -> set a property (e.g. Completed date), tick its checklist,
  notify, or run an Automation workflow.
- Live updates when a card changes elsewhere (the board's open in two places), recurring cards and
  card templates from the board, Mac app check, user guide.

**K6 done 28-10-26.** - Rules are Sentinel database rules (each an Automation workflow) with a new
`WhenPropertyBecomes` kind built on the trigger's new `changedTo` operator (needs `previousValues`,
now in every `database.rowChangedTrigger` payload) and `CardsOnly` (a `core.if` on `parentRowId`),
so a rule runs once as a card enters a column and never for sub-items. `MoveRowAsync` fires the
trigger now (drags started no rule before - a real gap for Sentinel's own rules too). Actions: set
a field (Person allowed now: `SaveInlineCellAsync` takes comma-separated usernames), complete its
sub-items (new `database.completeSubItems` node -> `IKanbanBoardService.CompleteSubItemsAsync`),
email, run a workflow. `KanbanAutomations.razor`: rules list, suggestions, builder; templates
(`AddCardFromTemplateAsync` names the card after the template - Sentinel templates leave titles
empty, so `CreateRowFromTemplateAsync` takes an optional title), repeats via `KanbanRepeats`
(four shapes <-> cron) on Sentinel row recurrences, which now run on the app's local zone
(`CronSchedule.GetNextOccurrence(..., zone)`; the UTC walk showed 08:30 as 04:30 here) and name
rows after their template. Column ⚡ chips from `SentinelDatabaseRuleView.WhenPropertyId/Value`
(read from the trigger node). Live: `WikiDatabaseChangeInterceptor` (SaveChanges hook) ->
`WikiDatabaseChangeNotifier` -> Board reloads 400 ms later unless the save came from its own
circuit's DbContext (`Origin`), waits out a drag (`isDragging`), lights changed cards (re-sort-only
rows excluded) and credits the person over the rule they set off. Mac check: Playwright WebKit
(drag, keys, drawer, live, Insights, dialogs) - found Escape dead in both board dialogs because
WebKit doesn't focus a clicked button; dialogs now take focus on open. Local WebKit login needs
the Secure auth cookie re-added without the flag (http only). Tests: end-to-end rules through the
real engine, changedTo cases, builder, repeats, the save hook (incl. failed saves), template
cards, local-zone schedules.

### K7 - Visual polish (Grant: "complete all", 27-10-26)
- Motion: FLIP glide when cards move, neighbours open a gap while dragging, a lift (shadow, tilt
  with the pointer) and a settle on drop; reduced motion turns it off.
- Sticky frosted column headers, a faint status-colour wash down each column.
- Board backgrounds (gradients, mesh, an image) and an accent colour per board, light and dark.
- Covers from the first image in a card's page; compact / comfortable / rich card sizes.
- Column WIP rings, card checklist bars, a sprint burndown sparkline in the header.
- Aging: cards fade and pick up an amber glow the longer they sit in progress.
- Presence rings on avatars of others viewing the board; a pulse on a card being edited.
- Zoom out (mini-map of a big board) and focus one column.
- Done celebration (a check burst), illustrated empty columns.

**K7 done 28-10-26.** - Look settings (`KanbanLook`: card sizes, 8 accents, 5 preset backgrounds +
an image, validated `IsImage` - https:// or a site path, nothing that can leave a CSS `url()`; the
service refuses anything else). The board root carries `kb-size-*`, `kb-bg-*` and an accent that
overrides `--gws-sidebar-accent` inside it. Covers fall back to the first image block; rich cards get
`KanbanCard.Snippet`. Columns scroll as a whole under sticky frosted headers with a colour wash
(the drag auto-scroll moved to the column). WIP ring (conic gradient), checklist progress bars.
Motion in `kanban-board.js`: lift + lean with pointer velocity, a gap that opens under the drop
point (positions from `offsetTop`, which ignores the gap's transform - rects mid-transition made
the drop point jump), FLIP glide after Blazor's render (a MutationObserver), settle on drop,
`celebrate` burst on `<body>` when a card enters a Complete column, `prepareGlide` for live
updates; all off with reduced motion. Zoom out; focus one column (others fold, still drop
targets; Esc leaves). Presence: `KanbanPresence` singleton (30 s check-ins, 90 s expiry) - ringed
avatars, a pulse on a card someone has open. Illustrated empty columns. Found live: a press on a
cover started a native image drag (covers now `draggable=false` + `pointer-events: none`, guarded
by a test); covers were clipped by a global `img { max-width: 100% }`; the drag placeholder stayed
opaque because pointer capture keeps it hovered. Burndown sparkline was already in the sprint bar
(K4). Checked in Chromium and WebKit (the Mac app's engine).

### K8 - Beyond the competition

K8 runs as five verified units: **K8a** one-line cards + forecasts; **K8b** SentinelGPT on the board;
**K8c** business links + time on cards; **K8d** My day + Mac capture; **K8e** public roadmap + votes.

**K8a done 28-10-26.** - `KanbanQuickParse` (!priority by name/prefix/number, #label, @person/@me,
~estimate, a trailing day word or dd-MM[-yy]; only what the board has; a one-word title is never
eaten) with chips under quick add; `AddQuickCardAsync` saves all fields in one `SaveRowAsync`, so
rules see them on the row-added trigger. `KanbanForecasting`: Monte Carlo (4,000 seeded trials)
over daily finishes since logging began (today excluded); `KanbanInsights.DailyDone/Forecasts`
(everything open, the running sprint, what's in progress) with a cumulative-chance chart in
Insights; `ForecastAsync(rowIds)` for the selection bar and the sprint bar's "85% likely done by".

**K8b done 28-10-26.** - `GetBriefAsync` gathers the facts (card moves since the last working day
with who made them, blocked / past-P85 cards, over-limit columns); `KanbanAssistant` turns them into
plain lines (shown as-is, and the whole prompt) and holds the prompts plus `ParseSteps` (JSON object
or array, else bullet lines; short, de-duplicated). `KanbanAssistantPanel` streams a write-up from
`BrowserLocalOllamaService` (this computer's Ollama, `LocalModels.PickChatModel`), 3-minute cap;
the drawer's Break it down uses `GenerateJsonAsync` and adds only ticked steps. Checked live with
gemma4 (standup in ~2 s). Also fixed: the drawer's Blocked checkbox was stretched into a bar by
`.kb-fields-inline > input` (K3).

**K8c done 28-10-26.** - `BoardCardLink` entity (row -> contact / ticket / invoice, no FK so a deleted
record shows as gone) + `TimeEntry.BoardCardId`, migration `AddBoardCardLinks`. `LinkAsync` (a ticket
or invoice also links its contact), `ListLinksAsync` (live titles/status), `SearchLinkTargetsAsync`,
`AddCardFromTicketAsync`, `OpenTicketsAsync`; board cards carry `LinkKinds` for their icons. Drawer:
Linked (chips with `data-preview` hover cards, search, unlink with Undo) and Time (start/stop through
`ITimeTrackingService`, billed to the linked contact, `ListForCardAsync`). Support's ticket header:
Add to board. Done -> a "Resolve" toast (`ToastState` gained `actionLabel`; a non-undo action no
longer says "Undone."). Found live: link icons were hidden on cards with no other footer details.

**K8d done 28-10-26.** - `/admin/my-day` gathers every open card assigned to the signed-in user
across boards into overdue / today / in-progress / next, alongside their open support tickets and
due CRM follow-ups; Done moves a card to its first Complete column with Undo. The Mac menu-bar
companion's Cmd-N quick add uses the same one-line parser through two `boards:write` endpoints,
remembers the last board, and queues unreachable writes in a local Application Support outbox for
the next successful refresh. The scope can only list boards and add a card. Focused Kanban tests
(93), the separate trimmed menu-bar Release build (x64 + arm64, zero warnings), and the local
server release gate passed; the gate is PARTIAL only because no deployed/responsive target was
supplied. My day also loaded cleanly through live authenticated Chromium and WebKit sessions with
the Knowledge navigation, six summary tiles and empty state intact and no console errors.

- One-line cards: "Fix login !high #bug @grant fri" -> priority, label, assignee, due, as chips
  while typing, no AI needed.
- SentinelGPT on the board (local Ollama, free and private): break a card into sub-cards and a
  checklist, a standup from the status log, "what's stuck and why".
- Forecasts: Monte Carlo from real throughput ("85% likely done by Friday") for a column, a
  sprint or a set of cards.
- Business links: make a card from a support ticket; link cards to contacts, tickets and invoices
  (RelationshipChip + hover previews); Done can resolve the ticket.
- Time on cards: start/stop a timer (Time Tracking) from a card; billable hours go to Billing.
- Rules through the Automation engine (K6) - no run caps.
- "My day": every card assigned to me across boards, plus tickets and follow-ups due today.
- Capture from anywhere on the Mac: menu-bar / hotkey quick add to a board; offline in the Mac app.
- Public roadmaps: publish a board read-only through Sentinel publishing, with votes.

Each phase is one verified unit (verify-release + a live browser check), in order.
