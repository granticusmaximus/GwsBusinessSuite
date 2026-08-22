# Site Building — User Guide

This is the complete guide to GWS Business Suite's Site Building cluster: **Pages** and
**Canvas Studio** (`/admin/pages`, `/admin/pages/edit/{id}`, `/admin/canvas/editor/{id}`) — a
drag-and-drop page builder for the live public site — plus **Appearance** (`/admin/appearance/customize`
and `/admin/appearance/menus`) for site-wide brand/design/navigation, and the **Media Library**
(`/admin/media`) that feeds images into all of it.

This guide is text-only (no screenshots) — see the note at the end of [`docs/USER_GUIDES.md`](USER_GUIDES.md)
for why, and how that may change later.

## Contents

1. [Core concepts](#core-concepts)
2. [The Pages list](#the-pages-list)
3. [Creating and configuring a page](#creating-and-configuring-a-page)
4. [Canvas Studio: the editor layout](#canvas-studio-the-editor-layout)
5. [Adding and arranging widgets](#adding-and-arranging-widgets)
6. [Editing widget content and style](#editing-widget-content-and-style)
7. [Freeform canvas positioning](#freeform-canvas-positioning)
8. [Global blocks](#global-blocks)
9. [Client Access — structural locking](#client-access-structural-locking)
10. [Visibility rules](#visibility-rules)
11. [Interactions and animations](#interactions-and-animations)
12. [Undo, redo, and autosave](#undo-redo-and-autosave)
13. [Workflow blueprints](#workflow-blueprints)
14. [Forms and form submissions](#forms-and-form-submissions)
15. [Revision history](#revision-history)
16. [Trash and page hierarchy](#trash-and-page-hierarchy)
17. [Site recipes (export and import)](#site-recipes-export-and-import)
18. [Appearance: Customize](#appearance-customize)
19. [Appearance: Menus](#appearance-menus)
20. [Media Library](#media-library)
21. [End-to-end tutorial: build a simple landing page](#end-to-end-tutorial-build-a-simple-landing-page)
22. [Known limitations](#known-limitations)

---

## Core concepts

A **site** is a single record (name, slug, theme, brand settings) that everything in this
cluster hangs off of. There can be more than one — a **Website** selector on Pages, Appearance
(Customize and Menus), and Settings lets you switch which site those screens operate on for the
rest of your session; whichever one you last selected stays current until you switch again or
start a fresh session, at which point it falls back to the `Canvas:SiteSlug` configuration value
(defaulting to `grantwatson-dev`, auto-created on first use if it doesn't exist yet). Canvas
Studio and the page-settings editor always stay bound to whatever site the page you're actively
editing already belongs to, regardless of the selector's current value. See
[Known limitations](#known-limitations) for the session-scoping details.

A **page** belongs to a site and holds: a title, a URL slug, an optional parent page (for nested
paths like `/services/consulting`), SEO fields, a publish status, and its actual visual
content — a JSON **layout** of **sections**.

- A **section** is a horizontal band of the page. It has a background, vertical padding, and
  either a **column layout** (Full Width, 50/50, 1/3+2/3, 2/3+1/3, or three equal thirds) or, in
  **Freeform** mode, a fixed-height canvas where widgets are placed by X/Y/width/height instead
  of flowing in columns.
- A **column** lives inside a section and holds an ordered list of **widgets**.
- A **widget** is one piece of content — a heading, a paragraph, an image, a button, a card, and
  so on. There are 14 widget types today (listed in [Adding and arranging widgets](#adding-and-arranging-widgets)).
- A **global block** is a widget or a whole section saved once and reused across pages. Every
  placement of it stays in sync with the saved original, except for whichever individual fields
  you've explicitly marked as "per-instance" (see [Global blocks](#global-blocks)).

Editing a page's content happens in **Canvas Studio** — a separate screen
(`/admin/canvas/editor/{pageId}`) from the page's settings screen
(`/admin/pages/edit/{pageId}`). Settings (title, slug, SEO, publish status, custom CSS) live on
one screen; visual content lives on the other, with a button linking each way. Studio autosaves
as you work — there is no separate "publish" step for content changes on an already-published
page; only the page's own Draft/Published **status** gates whether the page is reachable at all.

## The Pages list

`/admin/pages` lists every page on the site as a flat, indented tree (children shown nested under
their parent with an em-dash prefix). From here you can:

- **Search** by title or full path, and **filter** by status (All / Published / Draft).
- **Select multiple pages** (checkboxes) and **Move to Trash** them in one action. A page with
  active (non-trashed) child pages can't be trashed on its own — that page is skipped and its
  title is reported back to you, so the rest of the batch still goes through.
- Click **New Page** to create one, or click any row (or its **Edit** link) to open its settings.
- Switch to **Trash** to see everything you've moved there, with **Restore** and
  **Delete Permanently** per row.

A row shows a **Scheduled for …** badge (not just "Draft") when a page is Published but its
publish date/time is still in the future — see [Creating and configuring a page](#creating-and-configuring-a-page).

## Creating and configuring a page

Click **New Page** (or **Edit** on an existing one) to land on `/admin/pages/edit/{id}` — the
page's settings screen, distinct from its visual content. Fields here:

| Field | Notes |
|---|---|
| Page Title / Slug | The slug forms the URL segment. |
| Parent Page | Optional — nests this page's URL under a parent's (e.g. parent `services`, slug `consulting` → `/services/consulting`). A page can't be made its own descendant's parent — the dropdown excludes its own subtree. |
| SEO Meta Title / Description | Meta Title falls back to Page Title if left blank. |
| OG Image URL | Accepts an external URL or a `/media/{id}` reference from the Media Library. |
| Canonical URL Override | Falls back to the live page URL when blank. |
| Publish At | A future date/time keeps a Published page **scheduled** rather than live until that moment arrives — see below. |
| Category | Free-text with autocomplete against categories already used elsewhere on the site. |
| Tags | Comma-separated, free-form. |
| Client Edit Permission | The page-wide default for [Client Access](#client-access-structural-locking) — Open, Content only, or Locked. Individual sections/widgets can override it. |
| Page Custom CSS | Applies only to this page, layered after the site-wide CSS from Appearance > Customize. |
| Custom fields | Only shown if the site has any defined (Appearance > Customize > Custom fields) — typed per-page data (text/number/date/select/media reference) beyond the built-in fields. |

Buttons: **Save Page Details** saves everything above (and snapshots a revision first — see
[Revision history](#revision-history)). **Publish**/**Unpublish** flips status directly.
Publishing with a future Publish At date shows as **Schedule** instead of **Publish**, and the
status badge reads "Scheduled for …" until that time passes — nothing manual is needed at that
moment; a background sweep flips the page live and can fire a **CMS Page Published** automation
trigger (see the Automation guide). **Reset** clears the form back to a blank new page. **Move to
Trash** is only available once the page exists.

Once saved, a **View Live Page** link and an **Open Studio** button appear — Studio is where you
actually build the page's content (see next section). If the page isn't yet in the site's primary
navigation, an **Add to Nav** button appears to add it with one click.

## Canvas Studio: the editor layout

`/admin/canvas/editor/{pageId}` — reached from a page's settings screen via **Open Studio** — is
a three-panel workspace:

- **Left panel**, with four tabs:
  - **Widgets** — the widget palette. Click or drag a card onto the canvas.
  - **Global** — your site's saved global blocks (sections and widgets). Click or drag to insert.
  - **Reference** — a search box over the Builder Reference Library (curated workflow notes and
    suggested block types for a capability you're trying to build — populated separately under
    Tools > Builder Reference). This is documentation, not a code generator.
  - **Layers** — a collapsible tree of every section and widget on the page, with drag handles
    for reordering, and an **Add Section** button.
- **Center panel** — the **live preview**, an actual same-origin iframe rendering the real public
  page (with a small edit-mode bridge active). Desktop/Tablet/Mobile viewport toggles resize the
  frame; a reload button forces a fresh render. Clicking a section or widget directly in the
  preview selects it — the same selection state as clicking it in the Layers tree, kept in sync
  both ways.
- **Right panel** — the **Inspector**: whatever's selected (a section or a widget) shows its
  editable fields here. Nothing selected shows an empty state prompting you to click something.

The header shows section/widget counts, the page's Published/Draft pill, the current preview
viewport, Undo/Redo buttons, a manual **Save** button, and **Back** (which returns to the page's
settings screen, not the Pages list).

## Adding and arranging widgets

The 14 widget types, from the Widgets palette:

| Widget | What it holds |
|---|---|
| Hero | Full-width headline, subline, up to two CTA buttons, left/center alignment |
| Heading | H1–H4 text with alignment |
| Paragraph | Body copy (rich text editor) with alignment |
| Rich Text | A larger Markdown block (bold, links, lists, etc.) with a live rendered preview beneath the editor |
| Button | Label, URL, one of 6 style variants, 3 sizes, alignment, open-in-new-tab |
| Image | Source URL (or pick from the Media Library), alt text, caption, full/auto width |
| Card | Title, rich-text body, image URL, link URL |
| Testimonial | Rich-text quote, author name, author role/company |
| Accordion | An ordered list of question/answer items you add/remove/reorder freely |
| Form | A submit label plus a list of fields (text/email/tel/textarea/select/checkbox, each with a required flag; select fields also get comma-separated options) |
| Spacer | A configurable height in pixels (0–400) |
| Divider | Solid/dashed/dotted horizontal rule |
| HTML | Raw HTML, typed directly into a textarea — no sanitization, use with care |
| Posts Grid | A live grid of your most recently published blog posts — nothing to pick manually, it always reflects whatever's published when a visitor loads the page. Configurable count, columns, whether to show the hero image/excerpt, and the CTA label |

**Adding a widget**: click a palette card to drop it after whatever's currently selected (or at
the end of the last section if nothing is), or drag it directly onto a spot in the live preview,
or drag it onto a specific position in the Layers tree.

**Arranging widgets**: drag a widget's grip handle in the Layers tree to reorder it within a
column, into a different column, or into a different section entirely. The Inspector's action row
also has up/down move buttons, a **Dup** (duplicate) button, and delete, for whichever
widget/section is currently selected. Sections themselves reorder the same way (drag their header
in the Layers tree, or use the up/down arrows in the Section Settings panel).

Every structural change (add/move/delete/reorder, a new section, a column layout change)
autosaves and refreshes the live preview automatically — there's no separate "apply" step.

## Editing widget content and style

Selecting a widget opens its **Content** fields at the top of the Inspector (specific to that
widget type, per the table above), then a **Style** section that applies to any widget type:

- Text color and background color — either a direct color picker or, if the site has
  [design tokens](#appearance-customize), a token dropdown that overrides the picker and updates
  everywhere it's used if the token's value ever changes.
- Padding (None/Small/Medium/Large/XL), corner radius (None/Small/Medium/Large/Full pill), and
  font size (Default/Small/Medium/Large/XL, or a type-scale token the same way colors work).

For a handful of the most commonly-edited text fields — a Hero's headline/subline/CTA labels, a
Heading's text, a Paragraph's text, and a Button's label — you can also click the text directly in
the live preview and type; it saves back without reloading the preview frame. Every other field
(and every other widget type) is edited through the Inspector on the right.

Paragraph, Card body, Testimonial quote, and Accordion answers use the same rich WYSIWYG editor
used elsewhere in the admin suite (bold/italic/links/lists). Rich Text widgets are raw Markdown
with a rendered preview shown right below the editor. HTML widgets are exactly what you type,
unescaped.

## Freeform canvas positioning

A section's **Layout** setting can be switched from **Flow** (the default: widgets stack in
columns) to **Freeform** — a fixed-height canvas (configurable Canvas Height in px) where you
place widgets anywhere by dragging directly on the live preview, or by typing exact **X**, **Y**,
**Width**, **Height** (all percentages of the canvas) and a **Stack Order (Z)** in a selected
widget's Inspector when its parent section is in Freeform mode. Dragging on the canvas moves the
widget live; the final position is only persisted once you release the drag (no preview reload
needed for that). Switching a section from Flow to Freeform gives every widget already in it a
starting box automatically; switching back to Flow leaves those Freeform coordinates saved but
unused, so switching back to Freeform later restores the same arrangement.

## Global blocks

Any widget or section can be saved as a **global block**: select it, click **Save as Global** in
the Inspector, and name it. From then on it appears under the **Global** tab in the left panel,
draggable/clickable onto any page the same way a normal widget/section is. Every placement of a
global block stays in sync with the saved original — edit the block once (from any page that uses
it, via its Inspector) and every other placement picks up the change on next load.

If a global block needs to differ slightly per page (e.g. the same testimonial card everywhere,
but each instance's author photo should be swappable), check the fields you want as
**per-instance** in the "Per-instance fields" list shown on a global widget's Inspector — checked
fields can diverge on each page that uses the block; unchecked fields always stay shared.

A placed global block shows a **Global** badge with a **Detach** button — detaching converts that
one placement into an ordinary, independent widget/section with no further syncing. Deleting a
global block (from the Global tab, with a confirmation) doesn't touch content already placed on
pages — each placement keeps its own last-synced copy, it just stops syncing further.

## Client Access — structural locking

Every page has a default **Client Edit Permission** (Open / Content only / Locked), and any
section or widget can override it individually via its own **Client Access** dropdown, which also
offers **Inherit** (fall back to its parent — section falls back to the page; widget falls back
to its section, then the page). This exists so an Admin can hand a Contributor limited editing
rights on a page without risking the page's structure:

| Level | What a Contributor can do |
|---|---|
| Open | Everything — content, styling, moving, deleting |
| Content only | Edit the widget's text/image/link content, but can't move, delete, restyle, or (for a section) change its layout |
| Locked | No edits at all |

**Admins are never restricted** by this — it only constrains the Contributor role, and only in
Canvas Studio (the `[Authorize(Policy = "ContributorAccess")]` on these pages already keeps
everyone else out entirely). A Contributor viewing a restricted widget/section sees a small lock
notice explaining why fields are disabled, and — since a locked widget's own Client Access control
is itself inside the disabled fieldset — can never unlock something they don't already fully
control.

## Visibility rules

Independent of Client Access, every widget has a **Visibility** rule controlling whether it
renders for a real visitor at all: **Always show**, **Logged-in visitors only**, **Homepage
only**, or **Matching URL pattern** (a simple glob like `blog/*` matched against the page's full
path, where `*` matches any run of characters). A conditionally-hidden widget always still renders
inside Canvas Studio's own preview — with an edit-mode badge — so you never lose the ability to
find and edit it.

## Interactions and animations

Every widget can also carry one optional **Interaction** — a no-code trigger-and-animation
pairing, toggled on with the "Animate this widget" switch in the Inspector, right below
Visibility:

- **Trigger**: Page load, Scroll into view, Click, or Hover.
- **Animation**: Fade in, Slide in (from any of the four directions), Scale in, Bounce, or
  Pulse.
- **Duration** and **Delay**, both in milliseconds (0–10,000).
- **Play only once** — shown only for the Scroll into view trigger; unchecked, the animation
  replays every time the widget re-enters and leaves the viewport rather than firing just the
  first time.

A widget with an interaction always renders fully visible inside Canvas Studio's own preview —
the animation only ever plays on the live public page (and inside a downloaded Site Recipe
export, which is fully self-contained and doesn't need the live site to animate correctly). A
visitor with their operating system's "reduce motion" preference turned on sees the widget in its
final, fully-revealed state immediately, with no animation played at all.

Only one interaction is supported per widget today — there's no way to chain a sequence of
animations, or to combine two triggers on the same widget (e.g. "reveal on scroll, then also
bounce on click").

## Undo, redo, and autosave

**Ctrl+Z** / **Cmd+Z** undoes; **Ctrl+Shift+Z**, **Cmd+Shift+Z**, or **Ctrl+Y** redoes — both also
available as header buttons. This shortcut is deliberately suppressed while a text field,
textarea, select, or contenteditable element has focus, so it never fights with the browser's own
in-field text undo while you're mid-sentence. The undo stack holds up to 50 steps, lives only in
memory for the current session, and is lost on page reload — it is not the same thing as
[Revision history](#revision-history), which is server-persisted. Typing a burst of characters
into one field collapses into a single undo step rather than one per keystroke; any discrete
action (a click, a drop) always starts a fresh step.

Every change autosaves — most content edits after a short debounce, structural changes (adding,
deleting, or reordering something) almost immediately. There is no unsaved-changes state to lose
by navigating away, and no separate manual "Save" required for content to go live on an
already-Published page; the header's Save button exists for peace of mind, not necessity.

## Workflow blueprints

From a page's **settings** screen (not Studio), the **Workflow Blueprint** dropdown offers four
built-in starter layouts you can apply to the current page in one click:

| Blueprint | Category | For |
|---|---|---|
| Landing Page Conversion | Marketing | Hero, proof, feature stack, CTA funnel |
| Blog Editorial | Publishing | Header, table of contents, content, author box |
| Product Launch | Commerce | Pre-launch and launch-day conversion blocks |
| Service Business | Local Business | Lead-gen: service cards, trust signals, booking CTA |

Check **Replace existing blocks** to overwrite the page's current content instead of appending the
blueprint's sections after it; leave it unchecked to add alongside what's already there. These
compose the same 14 generic widget types listed above (there's no separate "pricing table" or
"countdown timer" widget under the hood — a blueprint approximates that kind of block from cards,
headings, and buttons).

## Forms and form submissions

A **Form** widget on a page posts its submissions back to that page's own submissions log —
visible on the page's **settings** screen under **Form Submissions**, showing every field/value
pair, a **New** badge for unread ones, **Mark read**, and **Delete**. There's no separate
form-builder screen; a form only exists as a widget on the page it's attached to, and its
submissions are scoped to that page.

## Revision history

Every **Save Page Details** on the settings screen snapshots the page's full state (title, slug,
content, SEO, custom CSS) as a numbered **revision** first, so any save is undoable. The
**Revision History** panel (page settings screen, once the page exists) lists every revision with:

- **Preview** — renders that revision's content inline, read-only, without touching the live page.
- **Diff** — a structural (added/removed/modified sections and widgets) comparison against the
  chronologically previous revision specifically — you can't diff two arbitrary revisions against
  each other, only each one against the one right before it.
- **Restore** — replaces the page's current content with that revision's. Restoring itself first
  checkpoints whatever was live as a brand-new revision, so restoring is itself undoable the same
  way any other save is.
- **Delete** — removes one revision permanently.

Only the most recent 20 revisions per page are kept; older ones are trimmed automatically after
each new save.

## Trash and page hierarchy

Deleting a page is two steps by design: **Move to Trash** (soft-delete, reversible) and, from the
Pages list's Trash view, **Delete Permanently** (hard-delete, not reversible). Both steps enforce
the same rule in opposite directions:

- You can't **trash** a page that still has *active* (non-trashed) child pages — trash the
  children first, or move them under a different parent.
- You can't **permanently delete** a trashed page that still has *any* child pages (trashed or
  not) — every child has to be gone first.

This means a parent/child relationship can never be silently orphaned by a bulk action; a blocked
page in a bulk Trash operation is skipped and reported by name rather than stopping the batch.

## Site recipes (export and import)

A **site recipe** (Appearance > Customize) is a portable JSON snapshot of everything structural
about a site — pages, categories, custom field definitions, global blocks, and design
settings — downloadable via **Export recipe**. A full **Export ZIP** (static asset export) is also
available alongside it.

**Import recipe** always creates a **brand-new site** with fresh page/category/field/global-block
ids — it never merges into or overwrites the current site — and every imported page starts as a
Draft regardless of its status in the original, so nothing goes live until you've reviewed and
explicitly published it. The new site shows up in the **Website** selector (see
[Core concepts](#core-concepts)) the next time you load a CMS screen, ready to switch into and
manage like any other site — see [Known limitations](#known-limitations) for the one caveat on
how long that selection sticks around.

## Appearance: Customize

`/admin/appearance/customize` holds everything that applies site-wide, across every page and the
blog:

- **Global Design** — an accent color and one of three built-in font pairings (Elegant:
  Playfair+Inter, Modern: Manrope+Inter, Classic: Merriweather+Source Sans).
- **Site-wide Custom CSS** — applies to every page; a page's own Custom CSS (from its settings
  screen) layers on top of this.
- **Logo URL** / **Favicon URL** — external URLs or `/media/{id}` references.
- **Design Tokens** — named **Colors** (a name plus a hex value) and a **Type scale** (a name plus
  a rem value), each addable/removable from simple inline forms. Once defined, any widget's Style
  panel in Canvas Studio can reference a token by name instead of a raw value — change the token
  here later and every widget referencing it updates everywhere, live site included.
- **Custom fields** — site-wide typed field *definitions* (Text/Number/Date/Select/Media
  reference) that then become editable per-page on that page's settings screen. Defining a field
  here doesn't set its value anywhere; it just makes the field available.

All of the above save together via the page's **Save changes** buttons (there are two, one per
card, both saving the same underlying record).

## Appearance: Menus

`/admin/appearance/menus` edits two independent link lists: **Primary** (site header) and
**Footer**. Both work identically — **Add Link** appends a new "New Link" → `/` entry you then
edit in place:

- **Label** and **Href** (a path like `/services` or a full URL) as plain text fields.
- An "open in new tab" checkbox per link.
- Up/down arrows to reorder, and a trash icon to remove.

**Save changes** persists both lists together. Menu items are flat — there is no nesting/dropdown
support, so every link sits at the same level in both the header and footer.

## Media Library

`/admin/media` is the shared image library behind Posts, Content Studio, and every Canvas Studio
Image widget (and the Image widget's own picker button opens this same library inline, without
leaving Studio). It is intentionally shared across websites rather than filtered by the current
website, so an uploaded brand asset can be reused without storing duplicate copies.

- **Upload**: pick a file (PNG, JPEG, GIF, or WEBP — the actual file bytes are sniffed and
  validated regardless of what content-type the browser claims, and SVG is deliberately rejected
  since it's XML capable of carrying a `<script>` tag). One file at a time. An "Alt text" field
  above the picker applies to whatever you upload next. Default size cap is 8 MB per file
  (configurable in Settings). If that filename already exists (case-insensitive), the page asks
  whether to replace it. Confirming updates the existing asset in place, preserving its URL and
  every page reference; cancelling leaves the existing image unchanged and creates no duplicate.
- **Browse and search**: a responsive grid with a filename search box.
- **Details modal**: click any image to see its type, size, upload date, and full URL (with a
  one-click Copy button), and to edit its alt text.
- **Delete**: permanent immediately — the confirmation dialog explicitly warns that any page still
  referencing the image will show a broken image, since deleting doesn't check or block on usage
  elsewhere.

## End-to-end tutorial: build a simple landing page

1. From `/admin/pages`, click **New Page**. Set Title to "Spring Promo" (slug auto-fills to
   `spring-promo`). Leave Parent Page as top-level. Click **Save Page Details** — this
   canonicalizes the URL and unlocks the rest of the screen.
2. Click **Open Studio**.
3. On the **Widgets** tab, click **Hero** to drop one in. Select it; in the Inspector, set a
   headline, subline, and a CTA label/URL.
4. Click **Add Section** (Layers tab) to add a second section below it. Select it, set its
   **Column Layout** to "Two Equal Columns (50/50)".
5. Drag a **Card** widget into the left column and an **Image** widget into the right column
   (or click each while that section is selected). Fill in the card's title/body, and use the
   Image widget's picker button to choose something from the Media Library.
6. Add a **Form** widget in its own section below that, with a couple of fields, so visitors can
   request more info.
7. Watch the live preview update after each change — no separate save/publish click is needed
   for content.
8. Back on the page's settings screen, set **Publish At** if you want it to go live at a specific
   time, then click **Publish** (or **Schedule**, if the date is in the future).
9. Open **Appearance > Menus**, add a link labeled "Spring Promo" pointing at `/spring-promo` to
   the Primary menu, and **Save changes** — or just use the **Add to Nav** button back on the
   page's own settings screen, which does the same thing in one click.

## Known limitations

- **Website selection is session-scoped.** Pages, new-page creation, Appearance, Menus, and the
  website identity fields in Settings all share the **Website** selector. Imported recipe sites
  appear in the selector on the next CMS screen load. The configured `Canvas:SiteSlug` is only the initial selection for a
  new login/session; a full reload starts from that configured site again. Canvas Studio and an
  existing page editor stay bound to the page's own website, while Media remains intentionally
  shared across every website.
- **No draft/live separation for content.** Unlike Workflow Automation's draft-vs-published
  version model, Canvas Studio autosaves straight to the live record. The only content-level
  safety net is Revision History (snapshot-before-save, with restore) — there is no "preview
  without publishing" mode for a page that's already Published.
- **Undo/redo is in-memory and per-session.** Capped at 50 steps, lost on page reload; it isn't a
  substitute for Revision History.
- **Revision diff only compares adjacent revisions**, not any two arbitrary revisions, and
  doesn't record which staff member made a given revision.
- **No page duplication.** You can duplicate a section or a widget inside Studio, but there's no
  "Duplicate Page" action from the Pages list or settings screen.
- **Fixed widget catalog.** 14 built-in types, no custom/plugin widgets, and no nested-container
  widget (a section's columns can't themselves contain another section) beyond what Freeform
  positioning offers within one section.
- **Menus are single-level.** No dropdown/nested sub-menus in either the Primary or Footer list.
- **Media Library**: single-file upload only (no batch upload), no folders/albums/tags beyond
  filename search, and no image cropping/editing. Duplicate filenames now prompt for in-place
  replacement, but legacy duplicates aren't consolidated automatically. Deleting an image never
  checks whether any page still references it.
- **HTML widget content is unsanitized** — it renders exactly what's typed, by design (it's meant
  for trusted authors embedding raw markup/embeds), so it isn't a safe target for untrusted input.
- **Client Access is a role/permission-level control, not a per-user ACL.** It distinguishes Admin
  from Contributor generally; there's no way to grant one specific Contributor access to one
  specific page the way Workflow Automation's per-workflow sharing does.
