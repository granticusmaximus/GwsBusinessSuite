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

## Decisions for Grant before R1

1. The workspace names and which app goes where (proposal below).
2. On the rail: icons with a hover label only, or icons with a short label under each.
3. The phone bar's four slots (proposal: Home, Inbox, Search, Create).

Proposed workspaces (the report's 7):

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

## R2. One page anatomy

- A `PageHeader` component: breadcrumb (workspace › app › record), title, one primary button,
  an overflow menu, then an optional filter/view row. `AppTabs` folds into it.
- Empty-state component: what goes here and the first action.
- Migrate pages in batches by workspace; each batch is a phase-sized unit with screenshots.

## R3. List and inspector

- A `RecordInspector` that slides in from the right over or beside the list, sharing the right
  edge with the SentinelGPT panel (one open at a time below 1440px; side by side above).
- First adopters, where it pays most: Support tickets (today's two-column view), CRM contacts
  (today a separate page), Billing invoices (today expand-in-place).
- Saved views per list (filters + columns), shared per role; sticky headers; a column chooser.
- The SentinelGPT panel gets the inspector's record as its context automatically (the
  `AssistantContext` already works this way).

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
