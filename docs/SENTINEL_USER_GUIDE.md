# Sentinel & SentinelGPT — User Guide

This is the complete guide to **Sentinel** (`/admin/sentinel`, nav label "Sentinel" — knowledge
workspace) and **SentinelGPT** (`/admin/sentinel-gpt`, nav label "SentinelGPT" — your private GWS
assistant). Sentinel is a clean-room, Notion-style workspace built into GWS Business Suite: pages
and databases with rich property types, multiple views, formulas, comments, sharing, and version
history, all stored locally in this app. SentinelGPT is the AI assistant that reads and (with your
confirmation) writes into that same workspace, running entirely on this app's own self-hosted
Ollama models.

This guide is text-only (no screenshots) — see the note at the end of
[`docs/USER_GUIDES.md`](USER_GUIDES.md) for why. For the engineering side of how these two areas
were designed and built, see [`SENTINEL_REBUILD.md`](SENTINEL_REBUILD.md) (architecture, delivery
plan, release scorecard) and [`WIKI_NOTION_CLONE.md`](WIKI_NOTION_CLONE.md) (capability matrix and
full-clone parity contract) — this guide is the operator-facing companion to both.

## Contents

1. [Core concepts](#core-concepts)
2. [Creating and editing a page](#creating-and-editing-a-page)
3. [The block editor and block types](#the-block-editor-and-block-types)
4. [Creating a database and its property types](#creating-a-database-and-its-property-types)
5. [Database views, filtering, and sorting](#database-views-filtering-and-sorting)
6. [Sub-items, relations, and linked databases](#sub-items-relations-and-linked-databases)
7. [Templates](#templates)
8. [Comments and discussions](#comments-and-discussions)
9. [Version history, trash, and duplication](#version-history-trash-and-duplication)
10. [Search and the command palette](#search-and-the-command-palette)
11. [Sharing and permissions](#sharing-and-permissions)
12. [Notion import and sync](#notion-import-and-sync)
13. [Using SentinelGPT](#using-sentinelgpt)
14. [The in-page "Ask SentinelGPT" panel](#the-in-page-ask-sentinelgpt-panel)
15. [Known limitations](#known-limitations)

---

## Core concepts

A **page** is a title, an optional icon and cover image, and a body made of **blocks**. A
**database** is a structured collection of **rows**, each defined by typed **properties**
(columns) and presented through one or more **views**. Under the hood a database row *is* a page
too — it has its own icon, cover, and block body — which is why opening a row feels the same as
opening any other page.

- **Block** — one unit of content (a paragraph, heading, image, embedded database, etc.). Every
  block has a type, an indent level, rich text (with bold/italic/strikethrough/code/color/links),
  and type-specific settings.
- **Property** — one typed column on a database (Text, Number, Select, Formula, Relation, …).
  Every database has exactly one required **Title** property; every other property type can be
  added any number of times.
- **View** — a saved way of looking at a database's rows: a type (Table, Board, Calendar, …) plus
  its own filters, sorts, and (for Board) grouping. Multiple views can exist side by side on the
  same database without duplicating any data.
- **Workspace** — your private page/database tree, shown in the left sidebar under "Private."
  Sentinel also has a lightweight "Teamspaces" entry, which today only ever represents a connected
  Notion workspace rather than a full multi-team structure.
- **Sharing** — two independent layers: per-user permission grants (View / Comment / Edit / Full
  access) on a specific page or database, and separate public share links for read-only,
  no-login access.

Two authorization policies gate these two areas differently: Sentinel itself requires
**ContentAccess**, while SentinelGPT's full chat page requires **AdminOnly**. Every content user
can open and edit the workspace; only admins get the standalone chat, tool-calling, and Deep
Analysis experience (non-admin users still get the smaller in-page assistant panel — see
[The in-page "Ask SentinelGPT" panel](#the-in-page-ask-sentinelgpt-panel)).

## Creating and editing a page

Click the **+** button above the sidebar tree for a blank page, or use the dropdown next to it to
start from a built-in starter template. A new page drops you straight into the editor.

- **Title** — the large text field at the top; typing auto-generates a matching **slug**, which
  you can also edit directly under "Page properties" (a collapsible section that also lets you
  change the page's **parent** in the tree).
- **Icon** — click the icon button next to the title to pick a common emoji, paste any emoji of
  your own, or remove it.
- **Cover image** — "Add cover" lets you upload a new image or pick one already in the media
  library; once set, hover the cover for "Change cover" / "Remove."
- **Body** — everything below the title is the block editor (see the next section). A hint line
  reminds you of the three text-entry shortcuts: `/` for commands, `[[` for a page link, `@` for a
  mention.

Edits **autosave** a few seconds after you stop typing — the status next to the page's breadcrumb
shows "Saved" once it lands — and there's also an explicit **Save changes** button. If someone
else saves the same page while you're still editing, a banner appears with three choices: reload
their latest version, save your draft as a new copy, or overwrite theirs with yours. Live presence
(who else currently has this page open) shows next to the title.

Other page-level actions live in the toolbar: **Duplicate**, **Trash** (soft-delete, recoverable —
see [Version history, trash, and duplication](#version-history-trash-and-duplication)), **Export**
as Markdown, and a star to toggle **Favorite**. In the sidebar tree itself you can drag a page onto
another to reparent it, use the up/down arrows or drag handle to reorder siblings, and multi-select
with the row checkboxes to bulk-move or bulk-trash several pages (and their sub-pages) at once.
Drag a page from that same tree into the open document canvas to insert a visual linked-page card
without moving the original page. The card opens the existing page; it does not copy its content.

## The block editor and block types

Every block is a typed unit with rich text and (where relevant) its own settings. Type `/` on an
empty or partial line to open the block menu, grouped as follows:

**Basic blocks**

| Block | What it does |
| --- | --- |
| Text (paragraph) | Plain paragraph text — the default block. |
| Page | Creates a nested sub-page and opens it. |
| To-do list | Text with a clickable checkbox. |
| Heading 1 / 2 / 3 | Section headings, three sizes. |
| Table | A simple, standalone text table (not a database). |
| Bulleted list / Numbered list | Ordinary lists. |
| Toggle list | Collapsible content behind an arrow. |
| Quote | Large text with a vertical accent line. |
| Divider | A thin horizontal rule. |
| Link to page | Lists every page you can access and inserts the selected page as a visual shortcut card. |
| Callout | Text in a highlighted banner with a custom icon. |

**Media**

Image, Web bookmark (a link-preview/embed card for a handful of recognized providers), Video,
Audio, PDF, and File (upload-and-download attachment) each get their own block and renderer, plus
a dedicated **Code** block for syntax-formatted snippets.

**Database inline / full page**

"Table view" embeds an existing database as an inline grid right in the page; "Linked database"
shows an existing database's view elsewhere without duplicating it; "New database" creates a brand
new database nested under the current page and opens it. In every case the database's schema and
rows live in exactly one place — a page only ever holds a reference and a display snapshot.

**Advanced & inline blocks**

Table of contents (auto-generated from the page's own headings), Block equation (LaTeX), Synced
block (editing any one copy updates every duplicated instance of it), Button (renders as a styled
link to a URL you set — see [Known limitations](#known-limitations) for what it doesn't do),
Mention a person, Columns (side-by-side layout), Tab (switchable panes), and Breadcrumb (shows the
page's own location).

Selecting a block reveals a drag handle with **Duplicate, Indent, Outdent, Move up, Move down,**
and **Delete**. Selecting text opens an inline formatting toolbar (**Bold, Italic, Strikethrough,
Inline code**, plus text/background color). Typing `[[` opens page-link autocomplete; typing `@`
opens a person-mention picker. `Tab` / `Shift+Tab` indent and outdent the current block.

The system-managed User Guides landing page (including if you rename it to “GWS Documentations
and Tutorials”) is rebuilt on startup with one visual linked-page card for every repository-backed
guide. Those cards and the pages beneath them stay synchronized with `docs/`; edit the source
guide rather than the mirrored Sentinel copy.

## Creating a database and its property types

Create a database from the sidebar **+** (grid icon), from a page's `/` menu ("New database"), or
by duplicating an existing one. A new database starts with a Title property and one Table view.

Open **Properties** to add columns. Every property has a name and a type; the addable types are:

| Type | Notes |
| --- | --- |
| Text, Number, Checkbox, URL, Email, Phone | Plain typed values. |
| Select, Multi-select | Single or multiple choice from a set of colored options you define. |
| Status | Single-choice like Select, but each option also belongs to a To-do / In progress / Complete group. |
| Date | A single date/time value. |
| Person, Files | Free-text, comma-separated values today (see [Known limitations](#known-limitations)). |
| Place | A free-text address, used by the Map view. |
| Formula | A computed value from an expression (below). |
| Relation | Links rows to another database. |
| Rollup | Pulls/aggregates a value through a Relation. |
| Created time, Last edited time, Created by, Last edited by | Auto-populated and read-only, never hand-set. |
| Unique ID | Auto-numbered once per row at creation, with an optional prefix. |
| Verification | A toggleable "Verified / Not verified" stamp recording who verified it. |
| Button | Runs a chosen Automation workflow when clicked on a row. |
| AI field | Text generated on demand by an Ollama model against a prompt template you write — never generated automatically. |

**Formulas** reference other properties as `[Property name]` and support logic (`if`, `and`, `or`,
`not`, `empty`, `coalesce`), number functions (`+ - * / % ^`, `round`, `abs`, `ceil`, `floor`,
`min`, `max`, `pow`), text functions (`concat`, `length`, `lower`, `upper`, `trim`, `contains`,
`startsWith`, `endsWith`, `replace`), and date functions (`now`, `today`, `dateAdd`,
`dateBetween`, `formatDate`) — e.g. `if([Done], "Complete", upper([Status]))`.

**Relations** link rows to another database; turn on a reciprocal property to keep both sides in
sync automatically (and rename or remove that reciprocal property later). **Rollups** pick a
Relation property, a target property on the related database, and an aggregation — Count, Count
values, Sum, Average, Minimum, Maximum, Show unique, Count empty, Count not empty, Percent empty,
Percent not empty, Median, or Range.

Other database-level tools live in the toolbar: **Duplicate**, **Templates** (row templates — see
[Templates](#templates)), **Import** (append rows from a CSV — first column becomes the Title,
matching headers reuse existing properties, unmatched headers become new Text properties, up to
5 MB), **Export** (download every row as CSV), **Lock** (freezes shared structure, views, and row
templates while individual rows stay fully editable — useful once a schema is settled but people
are still filling in data), and **Trash** (for the whole database, plus a separate panel listing
individually trashed rows).

## Database views, filtering, and sorting

Views appear as tabs across the top of a database. Beyond the default Table view you can add:

- **Board** — groups rows into columns by a **Select** property's options (a Status property
  can't be used for this — see [Known limitations](#known-limitations)).
- **List** — rows with inline property editing and expandable/collapsible sub-items.
- **Gallery** — a card grid.
- **Calendar** — a month grid keyed to a Date property, with an "Undated" bucket for rows without one.

Six further "advanced" view types cover more specialized presentations, each falling back to a
short prompt if the database doesn't yet have the property it needs:

- **Timeline** — rows plotted on a horizontal schedule by a Date property; drag a bar to
  reschedule, and optionally link rows as dependencies of one another.
- **Chart** — bar, line, or donut chart bucketed by a Select, Multi-select, Checkbox, Text,
  Formula, or Rollup property.
- **Form** — a simple entry form built from the database's own properties, for adding one row at a time.
- **Map** — plots rows with a Place property on an OpenStreetMap-based map.
- **Feed** — a reverse-chronological activity list showing each row's most recent update and a
  few of its properties.
- **Dashboard** — small stat tiles: total rows, rows updated this week, a checkbox count, and
  totals for up to three numeric/Formula/Rollup properties.

Every view has its own **View options**: a simple filter builder (property, operator, value) or a
nested AND/OR filter group for more complex logic, plus one or more sorts. A "Personalize filters"
toggle lets an individual teammate layer their own filters/sorts on top of a shared view without
changing what anyone else sees. A **Page properties** control lets you choose which properties
show under a row's title when it's opened, and in what order, and a separate toggle picks how
database pages open: **Side peek**, **Center peek**, or **Full page**. Table (and similar grid
views) also show a per-column footer where you can turn on Count / Sum / Average / Minimum /
Maximum for that column.

## Sub-items, relations, and linked databases

Any row can have **sub-items** added beneath it — nested rows within the *same* database, shown as
an expandable tree in List view. Deleting a parent row promotes its sub-items to top-level rows
rather than deleting them. This is distinct from a **Relation**, which links rows *across* two
different databases (with an optional reciprocal property keeping both sides in sync), which in
turn is what a **Rollup** reads through to pull or aggregate a value from the related side.

A database's canonical schema and rows always live in one place. A page can reference that same
database three ways from its `/` menu: **New database** creates and nests a fresh one under the
current page; **Table view** embeds an inline grid of an existing database right in the page's
blocks; **Linked database** shows an existing database's view without owning any of its data. None
of these ever fork or duplicate the underlying rows.

## Templates

Sentinel has four independent template mechanisms, all managed from the **Templates** panel in the
sidebar:

- **Page templates** — save any open page's current content (title, blocks, icon, cover — even
  unsaved editor changes) as a reusable starting point for new pages.
- **Block templates** — save just the blocks currently in the editor as a reusable snippet you can
  insert into any other page later.
- **Database templates** — snapshot an entire database (properties, rows including their page
  bodies, and views) as a template; using one always creates a fresh, independent copy with new
  internal ids.
- **Row templates** — per-database only: save one existing row's page content and reusable
  property defaults (e.g. a "Standard task" starting point) so new rows in that same database can
  start from it.

The same panel also hosts Notion workspace/template import — see
[Notion import and sync](#notion-import-and-sync).

## Comments and discussions

Every page has a **Discussions** panel. Start a discussion on the entire page, on a specific block
(picked from a dropdown), or on an exact selection of text within the page — selecting text quotes
that passage at the top of the new thread. Threads support nested replies, emoji reactions
(👍 ❤️ 🎉 👀 ✅), and **Resolve/Reopen**; a toggle shows or hides resolved threads, and author/date
filters narrow a busy discussion list. `@mentioning` someone in a comment creates an in-app
collaboration notification (bell icon with an unread badge) and surfaces the page under that
person's "Mentions" quick-navigation section in the sidebar. Live presence on the page shows who
else currently has it open.

## Version history, trash, and duplication

Every save creates a numbered **revision**. Every version from the last 90 days is kept; older
ones are thinned to one per day. You can diff any two adjacent revisions or **Revert** to a past
one. Database rows carry the same bounded revision history for their own page body.

**Trash** is a soft delete: trashing a page also trashes its sub-pages, and trashing a database
also hides its rows (without touching them) — everything reappears automatically on restore. The
Trash panel (badge shows the current count) lets you **Restore** or permanently **Delete** each
item. Database rows can additionally be trashed one at a time from inside their own database, with
a "Trashed rows" panel scoped to just that database. **Duplicate** is one click on any page or
database.

## Search and the command palette

The sidebar search box and the `⌘K` / `Ctrl+K` command palette both run the same full-text search
across page titles and block content, and database titles, property names, and row values/content
— scoped to whatever you're actually permitted to view. Results are labeled by match kind, support
author and date filters, and highlight the matched terms. Press `⌘K`/`Ctrl+K` from anywhere in
Sentinel to open the palette; arrow keys navigate results. You can bookmark a query as a
**Saved search** for one-click reruns later. When the search box is empty, the sidebar instead
shows **Favorites**, **Recent** (your last-opened items), and **Mentions**. Every page also has a
**Backlinks** panel listing every other page that links to it.

## Sharing and permissions

Each page or database has its own **Share** control with two independent layers:

- **Invite a user** — grant a specific username one of four access levels: **View**, **Comment**,
  **Edit**, or **Full access**. Remove a grant anytime.
- **Public link** — a read-only link anyone can open without logging in. Optionally set an expiry
  date/time and a password (locked out after repeated wrong guesses); optionally allow search
  engines to index it. View count and last-viewed time are tracked, and the link can be revoked at
  any time. The secret token itself is shown to you only once, at creation.

This same public-share plumbing also backs Workflow Automation's public status pages (see
[`AUTOMATION_USER_GUIDE.md`](AUTOMATION_USER_GUIDE.md)) — one mechanism, three kinds of target
(page, database, or workflow status) behind it.

## Notion import and sync

Sentinel can connect to a real Notion workspace via OAuth from the "Teamspaces" entry in the
sidebar. Once connected you can sync everything or pick specific pages/databases, run a manual
**Sync now**, or rely on a webhook for near-real-time updates when the Notion side changes. A page
can be pushed from Sentinel back up to Notion, and new local database properties can be pushed to
a Notion-synced database's schema. If both sides changed the same content, the conflict is queued
for you to resolve manually rather than silently merged.

Separately, the Templates panel offers two one-time import paths: **Restore a Notion workspace**
uploads a Notion "Markdown & CSV" or HTML export ZIP (up to 250 MB) and creates or updates real,
independently-editable pages, databases, rows, and files from it — re-uploading the same export
later updates matching documents instead of duplicating them. **Import a free Notion template**
duplicates a small ZIP (up to 25 MB) into your connected Notion workspace so you can pull it in
through ordinary sync and save it as a Sentinel template.

## Using SentinelGPT

SentinelGPT's standalone chat (`/admin/sentinel-gpt`, admin-only) runs entirely on this app's own
self-hosted Ollama models — nothing leaves your infrastructure unless you explicitly turn on web
research. The left rail lists your past conversations grouped by recency; **New chat** starts a
fresh one.

**Composer toggles**, all changeable per message:

- **Web** — includes live Ollama web search results in the prompt (disabled with an explanatory
  tooltip if the server isn't configured with a web-search API key).
- **Fast / Deep** — Fast asks SentinelGPT directly. **Deep** (the "Teacher Panel") first consults
  two specialist advisers in parallel — a Qwen model for an engineering/code review and a
  DeepSeek model to audit the reasoning — and folds their advice into SentinelGPT's own answer,
  explicitly labeled as untrusted model opinion to be reconciled against verified data, not taken
  as fact.
- **Tools on/off** — lets the model call real tools mid-answer: `search_wiki` to search your
  Sentinel workspace and `get_page` to read one page's full text, both of which populate a
  **Sources** list under the final answer.
- **Response length** — Concise, Standard, or Detailed.

**Grounding**: any non-trivial answer is grounded with previously approved "learning memory" (see
below), plus live search results when Tools or Web are on, plus the two specialist reviews in Deep
mode — all summarized under a **Sources** disclosure when citations exist.

**Governed write proposals**: ask SentinelGPT to change a database value, or to draft an automation
workflow in plain English, and it never writes directly. It returns a pending preview that pauses
the conversation until you **Confirm** or **Decline**. A confirmed workflow proposal is always
created as an inactive draft, never published or activated automatically (see
[`AUTOMATION_USER_GUIDE.md`](AUTOMATION_USER_GUIDE.md) for the rest of that flow).

**Learning memory**: use the thumbs-up/down under any answer to teach SentinelGPT — approved
answers become reusable context for future prompts; rejected ones are excluded. Nothing is learned
without your explicit review.

**Voice**: an optional mic button and spoken responses use the browser's own Speech API (available
in Chrome-class browsers; the controls simply don't appear where it's unsupported, e.g. Firefox).

**Model management**: the settings panel lets you switch the active local model, install (pull) a
new one by name, recheck the connection, and see live performance for the last response (time to
first token, tokens/sec, queue wait, model load time, total time). A failed run can be **Retried**;
an in-flight generation can be **Stopped**.

Separately, from any database row you can click **Autofill** to have SentinelGPT suggest values for
that row's empty properties — you review and choose exactly which suggestions to apply before
anything is written.

## The in-page "Ask SentinelGPT" panel

Above the block editor on every page (available to any content user, not just admins) is a
smaller, page-scoped assistant with six canned actions: **Ask, Summarize, Rewrite, Translate,
Research,** and **Meeting notes** — each just changes the placeholder and framing of your prompt,
not the underlying model. Its output is always explicitly grounded "in this workspace," shown as a
labeled draft with its model and sources, and it never touches the page until you click
**Approve & insert** (or **Reject**). A running generation can be stopped, and recent runs for the
current page are listed for reference.

## Known limitations

These are real gaps observed in the current implementation, not invented ones:

- **Person and Files properties are free-text.** Both store comma-separated plain values today —
  Person isn't tied to real GWS user accounts, and Files isn't backed by a real upload widget in
  the row grid (a row's own cover image does support upload; these two property types don't).
- **Board view groups only by Select, not Status.** A Status property carries its own
  To-do/In-progress/Complete grouping data, but Board view creation only offers to group by an
  existing Select property.
- **The page-level Button block is a plain link, not an automation trigger.** Despite its
  slash-menu description ("action macro scripts"), it renders as a styled hyperlink to a URL you
  set. Only a database's **Button property** actually runs an Automation workflow.
- **Map view does best-effort address lookup, not full geocoding.** It resolves whatever text is
  in a Place property against OpenStreetMap; unusual or ambiguous addresses may not resolve.
- **AI Field and row Autofill are always click-triggered.** Neither ever generates automatically
  on save, so an AI Field can sit unpopulated indefinitely until someone explicitly runs it.
- **SentinelGPT's full chat is admin-only.** Non-admin content users get only the in-page "Ask
  SentinelGPT" panel — not the standalone chat, tool-calling, Deep Analysis, or model management.
- **Formula, Relation, and Rollup values can't be set directly.** They're always computed/derived,
  in the editor and via CSV import alike (CSV import silently skips computed and system-managed
  columns).
- **Public share links cover pages, databases, and workflow status pages only** — there's no way
  to publicly share a saved search or the workspace as a whole.
- **"Teamspaces" is effectively single-purpose today.** It surfaces one entry for a connected
  Notion workspace rather than a real multi-team/multi-space structure inside Sentinel.
