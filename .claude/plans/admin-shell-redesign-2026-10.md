# Admin shell redesign (planned 19-10-26)

Source: the "Admin portal redesign" section of the GWS Suite App Review
(https://claude.ai/artifact/3DDhLvvV9abk6KzMBkm2Uf). Grant chose "Full redesign plan" on 19-10-26:
the nav merge shipped as top-10 phase 10, and this file plans the rest, to build over later
sessions. Each phase is a finished, verified unit (tests, live check at desktop/1200px/phone,
user guides, `./scripts/verify-release.sh`), and each leaves the app fully usable, since Grant can
push at any point.

**Starting point (after phase 10):** a top bar (⌘K, theme, SentinelGPT panel, bell, account) plus
a sidebar of 6 collapsible groups and 38 entries. Merged apps show a tab strip
(`AppTabs` + `AdminApps`). Grouping lives in `NavMenuGroups`, the entries in `NavMenu.razor`, and
the ⌘K palette reads the sidebar links plus hidden `.gws-command-extra` entries.

**Standing constraints:** admin breakpoints use `@container gws-main`; `@media` widths only
576/768/1024/1440/1920 (lint); CSP has no `unsafe-inline` (no inline scripts); dates dd-MM-yy and
24h times; zero warnings; the Mac app renders the same pages in WKWebView.

## Decisions (Grant, 19-10-26)

1. Workspaces are today's sidebar groups, so nothing moves: Home (Dashboard, Community),
   Content, Relationships, Knowledge, Intelligence, Growth, Platform. The report's
   People/Revenue split below was not chosen.
2. The rail shows an icon with a short label under each (about 64px wide).
3. The phone bar holds Home, Menu (the workspaces and pages as a sheet), Search (⌘K) and
   SentinelGPT. Notifications stay in the top bar; there's no Create menu yet.

The report's mapping, for reference (not chosen):

| Workspace | Apps |
| --- | --- |
| Home | Dashboard, Community |
| Publish | Posts, Content Studio, Content Calendar, SEO Audit, Localization, Comments, Form Submissions · Site: Pages, Site design, SentinelGPT Builder, Media · Shows: Podcasts, Live Show |
| People | Support, Users |
| Revenue | CRM, Billing, Time Tracking, Scheduling, Email Campaigns, Affiliate, Growth Studio |
| Knowledge | Sentinel, Mind Maps (Builder Reference reached from Pages) |
| Intel | Media Watch, Civic Watch, Business Intelligence, Overwatch, Threat Intelligence |
| Platform | Mission Control, Automation, Docker, Developer Tools, Security Audit, Privacy Operations, Settings |

Publish is the one workspace over ~8 links, so its sub-nav gets three labelled sections. Non-admin
roles see only Home and the Publish items they can use today, the same rules as `NavMenu`.

## R1. Workspace rail and sub-nav

- Replace the sidebar's groups with a 56px icon rail of workspaces plus a sub-nav column (about
  200px) for the current workspace. The workspace is derived from the route (extend
  `NavMenuGroups` into a route-to-workspace map, unit-tested like today).
- Rail: workspace icons, then pinned favorites and recent records at the bottom (stored per user;
  recents from the last N visited record URLs).
- Keep collapse: the sub-nav hides and the rail stays. On phones the rail and sub-nav become a
  bottom bar plus a full-screen sheet for the sub-nav.
- ⌘K keeps working (it reads links; update the selector for the new markup and keep
  `.gws-command-extra`).
- Risk: every page's navigation changes. Check all 38 entries open the right workspace, role
  gating, enhanced navigation (the layout is static SSR), Overwatch's `data-enhance-nav="false"`,
  and the Mac app.

**Done 19-10-26 (rail, sub-nav, collapse, phone bar).** What shipped:
- `NavMenuGroups.Workspaces` + `WorkspaceFor(path)` (Home is the fallback; a workspace the role
  can't see also falls back to Home). `NavMenu` renders a `.gws-rail` and one
  `section[data-workspace]` per workspace in `.gws-subnav`; app.js `showWorkspace` switches
  sections client-side, and enhanced navigation re-renders the current page's one.
- The rail is 72px, not 56: the responsive audit fails text under 12px, and "Knowledge" at 12px
  needs ~64px. Sidebar 272px (72 + 200), collapsed 72px. Descriptions are hidden in the sub-nav
  but stay in the markup for ⌘K, which now groups by workspace.
- Settings is a rail button (admins) plus a ⌘K entry. The Platform "Mission Control" link went:
  that route only redirects to the dashboard (merged 2026-09), so it opened Home; ⌘K keeps a
  "Mission Control" entry pointing at the dashboard.
- Phone (<768px): `.gws-bottom-bar` in MainLayout (Home, Menu, Search, SentinelGPT); the top bar
  hides its own toggle/search/assistant buttons; the drawer sits between the bars.
- Tests: `NavMenuGroupsTests` covers every admin route's workspace and parses NavMenu.razor so
  every link sits in the workspace its route opens. The responsive audit's navigation/search
  interactions pick whichever trigger is visible.
- Not checked: the Mac app's WKWebView (same pages, so expected to match; check under R7).

## R1b. Pins and recents on the rail (done 26-10-26)

Design (26-10-26): the layout is static SSR, so the rail's shortcuts are an interactive island
(`RailShortcuts`, like AssistantPanel/NotificationBell) on the same circuit as the page; a scoped
`ShortcutsState` carries "changed" from the page's PageHeader to the island, so pinning shows at
once. Table `UserShortcuts` (Username, Kind pin|recent, Url, Title, Icon, LastUsedUnix - a long,
since SQLite can't order DateTimeOffset). Pins: a pin button in every PageHeader (interactive
pages only), up to 4 shown as rail items. Recents: PageHeader `Record="true"` pages (article,
form submission, contact, page editor, draft, alert campaign, container, staff profile) record a
visit; the rail's "Recent" button opens a list of the last 8 plus the pins (with Unpin).

**Done 26-10-26.** `UserShortcut` + migration `AddUserShortcuts` (unique Username+Kind+Url),
`IUserShortcutService` / `UserShortcutService` (a DbContext per call from the factory: the page's
header and the rail island run on one circuit at the same moment, and the circuit's shared
IAppDbContext can't run two operations at once), `ShortcutsState`, `RailShortcuts` (rail items +
Recent list; backdrop closes it, it takes focus so Escape works - found when a removal took the
focused button away and Escape stopped reaching it), PageHeader pin button + `Record`. Rail
bottom group `.gws-rail-bottom` (the old `.gws-rail-end` went). Tests: `UserShortcutServiceTests`
(7). Checked live: pin shows on the rail at once, the container record shows under Recent, unpin
from the list updates the rail and the page's button, the rail pin link navigates, phone drawer
layout; responsive audit (phone/tablet/laptop) only the known Support "Open Ticket" under the bar.

Split out of R1: pinned pages and recent records at the bottom of the rail, stored per user.
Needs a per-user table (pins, recents) plus a migration; recents can come from pages that already
register a record with `AssistantContext` (Support ticket, CRM contact), and the pin button
belongs in R2's `PageHeader`, so build this after R2's component. Rail space: 8 items use
~470px, so about 4 pins/recents fit on a 900px-tall window before the rail scrolls.

## R2. One page anatomy

- A `PageHeader` component: breadcrumb (workspace › app › record), title, one primary button,
  an overflow menu, then an optional filter/view row. `AppTabs` folds into it.
- Empty-state component: what goes here and the first action.
- Migrate pages in batches by workspace; each batch is a phase-sized unit with screenshots.

**Components done 19-10-26** (`PageHeader`, `EmptyState` Title + first action, app.js
`data-gws-focus` scrolls to and focuses a page's own form field).

**Batch 1 done 20-10-26: Relationships (10 pages).** CRM, Deal Scoring, contact detail, Billing,
Time Tracking, Support, Scheduling, Email Campaigns, article-alert editor, Users. Conventions the
later batches should follow:
- The page's create action is the header's primary button. When its form is always on the page,
  the button resets the editor (`@onclick="ResetEditor"`) and carries `data-gws-focus="#field-id"`;
  the card's own "New" button (shown only while editing) went, since the header one does the same.
  When the form appears on demand (CRM quick create), the handler opens it and focuses the field
  from `OnAfterRenderAsync`. CRM's primary button follows its Contacts/Pipeline tab.
- Secondary toggles go in `More` as `.dropdown-item` buttons (Support's Email-to-ticket); small
  status lines and badges go in `Meta` (Support's satisfaction, the alert editor's status).
- Record pages use `ParentLabel`/`ParentHref` instead of a "Back to …" link.
- A list's empty state gets a `Title`, a sentence on how it fills up, and the same first action
  as the header. A filtered-empty list keeps a plain message.
- App tabs are 44px tall on touch screens (`pointer: coarse`) for the audit's target size.
- Checked with the responsive audit (phone-390, laptop-1280, desktop-1920) on a throwaway
  instance with seeded records: no overflow or small targets. Email Campaigns and Support still
  report "covered" controls on phones: mid-page form controls under the fixed bottom bar at load,
  reachable by scrolling.

**Batch 2 done 21-10-26: Content & Publishing (22 pages).** Posts list and record, Content
Studio and its draft, Content Calendar, SEO Audit, Localization, Comments, Form Submissions
(inbox and record), Pages, Edit Page, Builder Reference, Site design's three tabs, the Page
Builder and its queue, Media, Podcasts, Live Show and Past Shows. On top of batch 1's
conventions:
- The CMS site switcher goes in `Filters`; `Description` is a string, so a site name is
  interpolated (`@($"... {CurrentSite?.Name ?? "the live site"} ...")`). Header & Footer lost
  its inline Menus link, since Menus is the next tab.
- A sub-page reached from another page gets that page as its parent: Builder Reference ->
  Pages, Past Shows -> Live Show, Draft Workspace -> Content Studio, Edit Page -> Pages.
- On Pages, "Builder reference" stays a button beside New page instead of a More item.
  Contributors can open Pages, and an AuthorizeView inside More would leave them an empty menu.
- Media's "Upload image" uses `data-gws-focus="#media-upload"`; `focusField` now opens a
  file input's picker.
- **Fixed along the way (also affects batch 1):**
  - `focusField` lost its focus whenever the same click reset an EditForm's model. Billing's
    New invoice did this, as did the knowledge base's New source: EditForm rebuilds its
    fields for a new model. It now follows the field to its replacement. A negative control
    without the fix lost focus on both.
  - `.gws-empty-state i` also enlarged and greyed the icon inside an empty state's action
    button; it's now `> i`.
  - Sub-nav links were 40px on touch tablets; now 44px under `pointer: coarse`.
- Checked: the responsive audit at phone-390, tablet-768, laptop-1280 and desktop-1920 on a
  throwaway instance. Its only flag is a list row under the phone bottom bar at load (the
  known pattern). Playwright clicked every header action: the picker opens, focus lands, the
  More toggle, the new-page dialog, and a new article's record page and breadcrumb back.
  Content Studio can't be opened locally (device-secret policy), so its header-only change
  wasn't seen.
- Seen 21-10-26, fixed 25-10-26: `ProfessionalWysiwygEditor.DisposeAsync` threw
  TaskCanceledException when its circuit went away; it now ignores that as well as a disconnect.

**Batch 3 done 25-10-26: the last 23 pages, so R2 is complete.** Home (dashboard), Settings, the
5 Community pages, Workflow Automation + Credentials + Help, Docker Management + container
detail, Security audit, Privacy operations, Civic Watch, Media Watch, Threat Intelligence,
Business Intelligence, Growth Studio, Affiliate Analytics, Affiliate Suggestions, CJ Ads and
Mind Maps. Only the public booking page and the client-portal login keep `.page-header`. On top
of the earlier conventions:
- `PageHeader` has a `CssClass`; the four pages with the serif "rich" title (Civic Watch, Media
  Watch, Threat Intelligence, Growth) pass `page-header-rich`. The eyebrow went (the breadcrumb
  names the workspace); each page colours the breadcrumb, title and description from its own
  palette with `.<page-shell> ::deep .gws-page-header ...` rules, since scoped CSS doesn't reach
  into the child component. The `masthead-actions` rules went (PageHeader stacks its own actions).
- Growth Studio's title is now "Growth Studio" (the nav name); its old tagline is the
  description, the live-visitors pill is `Meta`, the Analytics/Social publisher switch is
  `Filters`. Threat Intelligence's Live intel/My exposure switch is `Filters` too.
- On-demand create forms (Departments, Messages): the header button opens the form and focuses
  its first field from `OnAfterRenderAsync` (CRM's pattern), and the form gained a Cancel button
  in place of the card's toggle.
- Secondary actions in `More`: Automation (New from template, Import, Credentials, Help),
  Credentials (Connect Slack/Google), Civic Watch (Settings toggle), Media Watch (Refresh All
  News only). Home keeps its three quick actions side by side.
- **Fixed along the way:** Threat Intelligence's stylesheet used `:global(...)`, which Blazor's
  scoped CSS doesn't support, so its card theming never applied (now `::deep`, which also reaches
  the My exposure panel). In the light theme, the active/hovered sub-nav link was white on
  near-white (now `--gws-sidebar-text-strong`), and Growth's always-dark shell showed dark
  headings and outline buttons (the shell now pins the theme tokens to their dark values).
  Automation's workflow cards printed `v@workflow.CurrentVersion` literally (Razor read it as an
  email address).
- Checked: the responsive audit (phone-390, tablet-768, laptop-1280, desktop-1920) on a
  throwaway instance over all 23 routes. No overflow, small targets or console errors; two
  phone "covered" flags (Settings, profile) are the known mid-page-field-under-the-bottom-bar
  pattern. Light-theme screenshots of the four rich pages, Home and Automation; Playwright
  clicked New department / New conversation (focus lands in the form, Cancel closes it) and
  every new ⋯ menu.

## R3. List and inspector

- A `RecordInspector` that slides in from the right over or beside the list, sharing the right
  edge with the SentinelGPT panel (one open at a time below 1440px; side by side above).
- First adopters, where it pays most: Support tickets (today's two-column view), CRM contacts
  (today a separate page), Billing invoices (today expand-in-place).
- Saved views per list (filters + columns), shared per role; sticky headers; a column chooser.
- The SentinelGPT panel gets the inspector's record as its context automatically (the
  `AssistantContext` already works this way).

**R3a built and browser-checked 26-10-26:** `RecordInspector` (fixed right panel, same
edge as SentinelGPT; Escape/close; focus on open; sets AssistantContext from `AssistantPage`),
`RightPanelState` (opening one closes the other unless `gwsAssistant.wide()` - 1920px+, not 1440:
at 1440 both leave ~350px of page), app.css makes room at 1440+ and side by side at 1920+.
Adopters: CRM contacts (`ContactInspector`: status, email, follow-up, open deals/tickets, unpaid
invoices, latest activity, SentinelGPT context) and Billing (title opens the invoice with facts,
notes, line items and the row's actions; replaces expand-in-place; `?invoice=` opens it).
Support kept its two-column view for now.
Checked on a disposable local instance: CRM inspector and assistant exclude each other at 1440,
sit side by side at 1920 (440px + 400px), and carry/clear the contact context. Resizing a live
dual-panel window down to phone width originally left both panels overlapping; a media-query
observer now closes the inspector and clears its record context when the layout narrows. Billing's
row inspector, Escape close and direct `?invoice=` open were also checked with a draft invoice.
**R3b built and browser-checked 26-10-26:** shared role-level list views persist filter JSON and selected columns
in `AdminListViews` (unique role + list + name, capped at 12), with `ListViewToolbar` providing
apply/save/update/delete and a column chooser. CRM Contacts supports search + status
and Name/Company/Email/Status columns restore from a saved view, Name cannot be hidden, and its
table header stays visible in the capped scrolling list. Checked live at desktop and phone: a
"Lead queue" survived reload, restored its filter/two-column layout, and logged no console errors.
Billing invoices now save search/status and Invoice/Client/Total/Due/Status columns; Support tickets
save status and Subject/Contact/Priority/Status/Tags columns. Invoice and Subject are always shown.
Checked both remaining adopters against the same disposable data: saved views survived reload and
restored their filters/columns, their tables became labeled rows at 390px, and the browser logged
no console errors or warnings. The persistence tests, Release web build, and breakpoint lint pass.

## R4. Visual system

- Audit tokens: a 4px spacing scale, flat surfaces with 1px borders, shadows only on floating
  layers, the brand orange for primary actions only, and semantic status colors.
- Density toggle (comfortable/compact) stored per user, applied as a class on `.gws-layout`.
- Mono face with `tabular-nums` for IDs, money, times and code in tables.

## R5. Status and feedback

- Status pills and row stripes (overdue, SLA breach, frozen camera) as shared components.
- Skeleton loaders instead of spinners on list pages; optimistic updates with an undo toast
  where an undo exists (delete, archive, status change).
- Notification bell becomes an inbox: done, snooze, filter by app.

## R6. Keyboard

- `g` then a letter jumps to a workspace, `j`/`k` move through lists, `?` opens a shortcut sheet.
- ⌘K becomes action-first: "new invoice for…", "open ticket …", backed by a small action
  registry next to the page list.
- All key handling lives in app.js (CSP), and stays off while typing in inputs and editors.

## R7. Mac app

- The native sidebar mirrors the workspace rail; ⌘1–7 switch workspaces.
- Check every R-phase in the Mac app's WKWebView before calling it done.

## Order and size

R1 is the structural change and goes first; R2 and R4 can follow in either order; R3 depends on
R2's header; R5 and R6 are independent; R7 follows R1. Rough size: R1 and R3 are a session each,
R2 is one session for the component plus a session per batch of about 10 pages, and the rest are
about half a session each.
