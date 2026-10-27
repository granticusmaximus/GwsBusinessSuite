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

### K3 - Flow control
- WIP limits per column (header "3 / 4", amber at the limit, red over it).
- Card aging: days in the current column on each in-progress card, faded dots after N days.
- Blocked: a flag with a reason, a red edge, and a Blocked filter.
- Swimlanes by Priority, Assignee, Label or Sprint (or none), collapsible, with counts.
- Filter bar: search, labels, assignee, priority, due (overdue / this week), "My cards"; saved
  per person (view personalization).

### K4 - Sprints
- Sprint planning: backlog beside the sprint; drag in, see the estimate total against capacity.
- Start / complete a sprint; complete moves unfinished cards to the next sprint (rollover), with
  a summary. Sprint goal on the board header; burndown (estimate or card count) in the header.

### K5 - Flow metrics
- Status-change log (new table): every column change on a board card, with time and who.
- Insights panel: cycle time (median, 85th percentile, scatter), throughput per week, cumulative
  flow diagram, aging WIP; per card, its time in each column.

### K6 - Automation and polish
- Rules: when a card enters a column -> set a property (e.g. Completed date), tick its checklist,
  notify, or run an Automation workflow.
- Live updates when a card changes elsewhere (the board's open in two places), recurring cards and
  card templates from the board, Mac app check, user guide.

Each phase is one verified unit (verify-release + a live browser check), in order.
