# Intelligence & BI — User Guide

This is the complete guide to GWS Business Suite's Intelligence cluster: **Media Watch**
(`/admin/news-intelligence`), **Civic Watch** (`/admin/government-intelligence`), the **Podcast
Directory** (`/admin/podcasts`), **Business Intelligence** (`/admin/business-intelligence`),
**OSINT Watch** (`/admin/osint`), and **Mind Maps** (`/admin/mind-maps`). The first five pages
don't share a database schema, but they share a shape: each one pulls in outside signal (news,
government sources, podcast catalogs, public OSINT) or your own suite data, and turns it into
something you can scan in a few seconds. Mind Maps is the one exception — it doesn't ingest
anything; it's a plain authoring tool for outlines and roadmaps you build yourself.

This guide is text-only (no screenshots) — see the note at the end of [`docs/USER_GUIDES.md`](USER_GUIDES.md)
for why, and how that may change later.

## Contents

1. [Core concepts](#core-concepts)
2. [Media Watch: watched topics and the sidebar](#media-watch-watched-topics-and-the-sidebar)
3. [Media Watch: reading the feed](#media-watch-reading-the-feed)
4. [Media Watch: refreshing and hot takes](#media-watch-refreshing-and-hot-takes)
5. [Civic Watch: the three desks](#civic-watch-the-three-desks)
6. [Civic Watch: legislation briefs and SentinelGPT overviews](#civic-watch-legislation-briefs-and-sentinelgpt-overviews)
7. [Civic Watch: refreshing and dark mode](#civic-watch-refreshing-and-dark-mode)
8. [Podcast Directory: discovering and saving shows](#podcast-directory-discovering-and-saving-shows)
9. [Podcast Directory: episodes, playback, and progress](#podcast-directory-episodes-playback-and-progress)
10. [Business Intelligence: building and pinning a chart](#business-intelligence-building-and-pinning-a-chart)
11. [OSINT Watch: map and public-source tools](#osint-watch-map-and-public-source-tools)
12. [Mind Maps: building an outline](#mind-maps-building-an-outline)
13. [Who can see what](#who-can-see-what)
14. [Known limitations](#known-limitations)

---

## Core concepts

Each page has its own vocabulary, but two ideas repeat everywhere in this cluster:

- **Shared vs. private.** Media Watch's watched topics, Civic Watch's snapshot, and the Podcast
  Directory's saved library are all *shared* across every admin — there's one set of watched
  topics, not one per staff member. The exceptions are Business Intelligence dashboards (each
  admin's pinned charts belong only to them), Mind Maps (each admin's saved mind maps are private
  to them too), and podcast listen progress (whether *you* finished an episode is tracked per
  staff account, even though the saved show itself is shared).
  OSINT Watch reads live public sources and can keep browser-local map preferences, but it does
  not save OSINT results into GWS or expose them to another staff account.
- **Background refresh vs. on-demand refresh.** The four native suite pages either refresh their
  source data on a schedule, recompute it when the page loads, or offer a manual refresh action.
  Where a scheduled and manual refresh share a source, they also share a lock so two runs cannot
  overwrite each other. OSINT Watch is different: the embedded dashboard polls its public sources
  while it is open rather than using a GWS background job.

## Media Watch: watched topics and the sidebar

Media Watch (`/admin/news-intelligence`) is a SentinelGPT-curated news reader built around
**watched topics** — saved searches you define once and come back to.

The left sidebar always has two built-in views plus one row per topic:

- **All News** — a flat, topic-free aggregator of general news (see below).
- **Breaking News** — the same pool, filtered to items whose title or description contains a
  breaking-news signal word (`breaking`, `urgent`, `alert`, `developing`, `live updates`, `just
  in`). This is a simple keyword heuristic, not a real editorial "breaking" flag from any source.
- **Your topics** — each with a colored dot, a strikethrough when inactive, and a badge showing
  how many articles are currently in that topic's feed.

Click the **+** button above the topic list to add one, or the pencil icon on an existing topic
to edit it. A topic has:

- **Name** and **Keywords** (comma-separated — these are what actually get searched).
- **Badge color**, used for the sidebar dot and the source-avatar circle on its cards.
- **Active (refreshes on schedule)**, shown while editing an existing topic. Turn it off to keep
  the topic and its current feed visible while excluding it from scheduled and Refresh All runs;
  turn it back on whenever you want refreshes to resume. New topics always start active.
- **Topic type**:
  - **General** — searches Google News RSS + dev.to. Best for regions, people, or current
    events.
  - **Technical / Programming** — searches Hacker News + dev.to instead, deliberately skipping
    Google News (keyword search on Google News is mostly noise for narrow programming terms like
    "Blazor" or "C#"). Best for languages and frameworks.

The trash icon deletes a topic and every article it's currently holding.

## Media Watch: reading the feed

**All News** and **Breaking News** both read from a shared, topic-free "Top News" pool — topic
articles never appear there, and vice versa. When more than one outlet is present in that pool, a
**source filter** dropdown appears above the grid: pick any number of outlets to narrow the view,
or search the outlet list itself by name. Selected outlets stay pinned to the top of that list
even if they stop matching your search text, so toggling one off is never blocked by whatever you
typed. dev.to is deliberately never listed as a toggleable outlet — it always shows regardless of
which outlets you've selected, since it isn't a traditional "news outlet" the way a Google-News
publisher is.

Each card shows the outlet's initial in a colored circle, the outlet name, how long ago it was
published, the headline (opens the original article in a new tab), a one-line take (see below),
and an "Open" link. Articles disappear from every view 24 hours after they were fetched — there's
no manual archive.

## Media Watch: refreshing and hot takes

Three refresh actions, all sharing one lock so they can't collide with each other or with the
hourly background refresh:

- **Refresh All** — refreshes the Top News pool and every *active* topic in parallel (up to 3 at
  once), then prunes any article older than 24 hours.
- **Refresh All News** — refreshes only the shared Top News pool behind All News/Breaking News.
- **Refresh now** (per topic, from the sidebar) — refreshes just that one topic. This only works
  while the topic is active. Use the pencil icon and the **Active (refreshes on schedule)** switch
  to pause or resume a topic without deleting it; inactive topics remain visible with
  strikethrough styling but are excluded from Refresh All.

While a refresh runs, a "Refresh pipeline" panel shows the current phase, a completed/total
progress bar, which feeds are actively being fetched, and a per-stage timing table (feed fetch,
Ollama summary, database commit) for the slowest twelve stages.

Every refresh fully replaces that topic's or pool's articles — it deletes the old set and
re-inserts whatever came back this time, rather than merging in just what's new. That also means
the AI "hot take" under each headline is regenerated from scratch every refresh: GWS asks the
configured Ollama model for one sharp, opinionated sentence (under 20 words) per article in a
single batched call, with a 60-second timeout. If Ollama is unavailable or times out, the articles
still save — they just fall back to showing the source's own description instead of a hot take,
silently, with no error shown on the page.

In the background, all of this also runs on its own: an hourly scheduled refresh (starting ~30
seconds after the app boots) keeps the feed from going stale even if nobody opens Media Watch.

## Civic Watch: the three desks

Civic Watch (`/admin/government-intelligence`) is a single, fixed civic briefing — **Kathleen,
Houston County, Georgia** — covering three "desks," selectable from the nav bar or by clicking a
signal card in the header:

- **Community** — Houston County government notices, the county's public meeting calendar, local
  events (scraped separately on their own hourly cadence by a background service; this page just
  reads that cache), a directory of county/school-district/public-safety resource links, and a
  "Local law" panel (see below).
- **Georgia** — the Governor's recent press releases, current-year signed legislation, and the
  Georgia House and Senate's most recent floor votes, pulled from the Governor's site and the
  Georgia General Assembly's own API.
- **Congress** — for both the House and Senate: whether the chamber is currently on the floor
  (with a live video embed only while actually in session), the most recent Congressional Record
  transcript for that chamber (official, but same-day-delayed — not live captioning) with an
  expandable archive of older transcripts, recent chamber news, and recent roll-call votes with
  Georgia's own delegation easy to spot.

The header always shows a source count per desk and when the briefing was last updated.

The Community desk's **Local law** panel is *not* a live feed of local legislation — it's two
static, hand-written research guides pointing you at Houston County's actual ordinance code and
commission calendar, with a suggested three-step research path (read the current code → check the
meeting calendar for pending action → watch the notices feed for adoption). Real live
pending-legislation tracking only exists at the Georgia and federal levels.

## Civic Watch: legislation briefs and SentinelGPT overviews

Any Georgia signed law or floor vote can carry a **Legislation brief** — an expandable panel with
a plain-language headline, summary, status, key facts, a timeline, and links to the official
source. Some of these also carry a **SentinelGPT overview**: a short AI-generated summary shown in
its own highlighted panel above the facts.

SentinelGPT overviews are never generated while you're looking at the page — they're produced by
a background job (part of the same 15-minute refresh cycle described below) that finds any Georgia
signed law or vote without a cached overview yet and asks SentinelGPT for one, one bill at a time,
then caches the result for 7 days. If a bill is brand new, or Ollama is temporarily unavailable,
its brief simply has no SentinelGPT panel yet — there's no error, and no manual "generate now"
button on the page itself. This only ever happens for Georgia state items; federal votes and
Community-desk items never get an AI overview.

## Civic Watch: refreshing and dark mode

**Refresh sources** forces a fresh pull of all three desks, bypassing the normal 15-minute cache.
It shares a lock with a scheduled background job that also refreshes the snapshot every 15
minutes (starting ~30 seconds after boot) — if the background refresh happens to be running when
you click it, you'll be told to try again in a moment rather than the click doing nothing.

The moon/sun icon in the header toggles dark mode, saved to your browser's local storage. This
preference is shared with Media Watch — the two pages use the same storage key, so switching to
dark mode on one carries over to the other.

## Podcast Directory: discovering and saving shows

The Podcast Directory (`/admin/podcasts`) has two tabs. **Discover** starts on a **Featured** view
— six curated categories (Comedy, True Crime, News, Technology, Business, Health), each showing
six shows pulled live from Apple Podcasts' public search API. Click a category chip (twelve are
available, a superset of the featured six) to browse that category more broadly, or type into the
search box for a free-text search across Apple's catalog. **Reset** returns to Featured.

Each result card shows a **Save to Library** button, unless the show doesn't expose an RSS feed
URL at all — in that case the button is replaced with a disabled "Feed unavailable" state, since
there's nothing to actually pull episodes from. If a show is already in your library, the card
shows a "Saved" badge and an "Open Library Entry" button instead of Save.

Saving is deduplicated across four possible identity keys (iTunes id, normalized feed URL,
normalized Apple URL, or a name+author pair) — finding "the same" show again through a different
search term and saving it won't create a second library entry; you'll just see "already in your
library."

## Podcast Directory: episodes, playback, and progress

The **Library** tab lists everything your team has saved, with a free-text search (title, author,
description) and a category filter built from whatever categories are actually present in your
library. Click **Open Episodes** to open a show's detail modal.

Episode data comes from the show's *own* RSS feed (not Apple), fetched with a standard feed
reader. It refreshes automatically if it's never been fetched, is more than 12 hours old, or
currently has zero episodes — or you can force it with **Refresh Episodes**. If a refresh attempt
gets nothing back (feed temporarily down, DNS failure, malformed XML), the directory keeps
whatever episodes it already had rather than wiping the list, and quietly retries on the next
scheduled check instead of waiting out the full 12-hour window.

Each episode has an inline audio player. Playback position is saved automatically as you listen
and is tracked **per staff account** — your own listening history, not a shared team state — with
an episode automatically marked "Listened" once you've reached about 95% of its duration. Episodes
you've made progress on but not finished show a small progress bar with a "Resume from…" label;
reopening the same episode picks up where you left off.

**Remove** (with a confirmation prompt) deletes a show and every one of its saved episodes from
the shared library for everyone, not just for you.

## Business Intelligence: building and pinning a chart

Business Intelligence (`/admin/business-intelligence`) lets you build small reports from
suite data and pin them to *your own* dashboard — every admin's set of pinned charts is separate.

Click **Build chart** to open the builder:

1. **Data source** — one of three fixed, application-owned query shapes (there's no raw SQL and
   no custom query builder):
   - **CRM deals** — deal count or pipeline value, grouped by stage or by month created.
   - **Article performance** — page views or unique visitors for published articles, from
     first-party web analytics, grouped by article (top 12).
   - **Affiliate revenue** — commission, sales, or actions from CJ affiliate data, grouped by
     advertiser (top 12).
2. **Metric** and **Group by (dimension)** — the options change based on the data source you
   picked.
3. **Chart title** — defaults to "`<Metric> by <Dimension>`," fully editable.
4. **Date range** — 7, 30, 90, or 365 days; nothing else is selectable.
5. **Visualization** — Bar, Line, or Table.

Click **Preview** to see it rendered before committing to anything, then **Pin to dashboard** to
save it. Every widget on your dashboard has an **Edit** and **Remove** option from its own
three-dot menu; editing reopens the same builder pre-filled with its current settings and a live
preview.

Chart data is recomputed fresh every time your dashboard loads — nothing is cached between visits,
so the "refreshed h:mm tt" label under each chart's total simply reflects that page load, not a
background job. As a performance detail: if your dashboard has several "CRM deals" widgets with
different date ranges, GWS still only queries the deals table once per page load (using the widest
range any of those widgets needs) and applies each widget's own narrower range afterward, rather
than re-scanning the table once per widget.

## OSINT Watch: map and public-source tools

OSINT Watch (`/admin/osint`) embeds the OSIRIS public-source intelligence dashboard inside the
admin portal. It combines live or recently reported aircraft, vessels, satellites, traffic
cameras, seismic and weather events, conflict data, cyber indicators, infrastructure, and other
open-source layers on one map. Availability and update timing vary by source.

Use the map's layer and tool controls to narrow the signals you want to inspect, select a marker
for its available source details, and use **OSIRIS reference** at the top of the page for the
upstream dashboard's feature and data-source reference. Some layers refresh while the dashboard
is open; an individual upstream source can fail without taking down every other layer.

Treat every match as an investigative lead, not a confirmed fact. Verify consequential findings
against an authoritative source before acting on them. Active RECON scanning is intentionally
unavailable in this deployment, and the dashboard must not be used to enter client data, API keys,
passwords, or other private material. For the reviewed build and integration boundary, see
[`OSINT_WATCH_SECURITY.md`](OSINT_WATCH_SECURITY.md).

## Mind Maps: building an outline

Mind Maps (`/admin/mind-maps`) is a plain visual outlining tool, separate from Wiki/Sentinel and
Automation — each mind map is its own saved document. Like Business Intelligence dashboards, your
mind maps are private to your own account; nobody else's admin login sees them.

From the list page, **New mind map** creates a document with a single root node (named after the
title you give it) and opens its editor. Inside the editor:

- Click a node to select it, then use **Add child**, **Rename**, or **Delete** in the toolbar —
  editing is toolbar-driven rather than double-click-in-place, so every change is explicit.
- **Delete** is disabled for the root node (a map always needs one root) and, for any other node,
  removes that node and all of its descendants after a confirmation prompt.
- The map title itself is editable from the header next to the back button and saves as soon as
  you click away from the field.
- Every add/rename/delete saves immediately — there's no separate "Save" step, and reloading the
  page always shows the tree exactly as you left it.

A real example ships out of the box: an **ASP.NET Core Developer Roadmap** mind map, seeded once
for the admin account and transcribed from the community-maintained
[AspNetCore-Developer-Roadmap](https://github.com/MoienTajik/AspNetCore-Developer-Roadmap) project
(21 topics from general development skills through production concerns like CI/CD and monitoring,
each with a few representative resources) — open it to see a populated map before building your own.

## Who can see what

All six pages require a staff sign-in. Five of them (Media Watch, Civic Watch, Business
Intelligence, OSINT Watch, and Mind Maps) are restricted to the **AdminOnly** policy specifically;
the Podcast Directory uses the slightly broader **ContributorAccess** policy, so a Contributor-level
teammate who isn't a full admin can still browse and manage the shared podcast library.

## Known limitations

- **No real "unread" tracking in Media Watch.** The badge next to each topic is just a count of
  how many articles currently sit in that topic's 24-hour window — opening a feed never marks
  anything as read, and there's no per-user read state.
- **"Breaking News" is a keyword heuristic**, not a real breaking-news signal from any source —
  it just scans titles and descriptions for words like "breaking" or "urgent."
- **Hot takes and episode/article sets are fully replaced on every refresh**, not incrementally
  updated — refreshing the same topic twice in a row can return a different set or order of
  articles even with identical keywords.
- **Civic Watch covers one fixed area** (Kathleen, Houston County, Georgia, plus Georgia state and
  U.S. federal government) — there's no per-user or per-tenant location configuration.
- **Civic Watch's "Local law" panel is static**, not a live feed — it's curated research guidance
  pointing at the county's own ordinance code and calendar, not automatically discovered local
  legislation.
- **SentinelGPT overviews only cover Georgia state legislation and votes** — never federal items
  or Community-desk content — and there's no manual "generate now" action if a bill hasn't been
  processed by the background job yet.
- **Podcast discovery depends entirely on Apple's public search API and each show's own RSS
  feed**, with no alternate provider if either is unreachable — a failed Apple search simply
  returns no results rather than surfacing a retryable error.
- **Business Intelligence report shapes are fixed.** Only the three built-in query shapes, their
  predefined metrics/dimensions, and the four preset date ranges are available — there's no custom
  query builder or ad-hoc field picker.
- **Business Intelligence chart rendering is hand-built**, not backed by a charting library — bar
  and line visualizations are simple SVG/CSS constructs, which is why data is capped to a small
  number of points (e.g. top 12 advertisers/articles) rather than rendering dense datasets.
- **OSINT Watch depends on third-party public feeds and two internal containers.** A feed may be
  stale, incomplete, misidentified, rate-limited, or temporarily unavailable, and the embedded
  dashboard requires both the `osiris` and `osiris-intel` services to be healthy. Active RECON
  scanning is not configured in GWS.
- **Mind Maps editing is toolbar-driven, not free-form.** You can't double-click a node to rename
  it in place or drag one node onto another to reparent it — every structural change goes through
  the Add child / Rename / Delete buttons. This is a deliberate limitation of the underlying
  widget's edit-event wiring, not a v1 shortcut awaiting a fix.
- **Mind Maps has no import from XMind or other formats.** The seeded roadmap example was
  hand-transcribed, not parsed from a file; building your own map means adding nodes one at a time.
