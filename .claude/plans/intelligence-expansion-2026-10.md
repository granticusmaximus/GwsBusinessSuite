# Intelligence section expansion (proposed 2026-10-05)

Grant asked for 5 improvements + 5 new features for each Intelligence page. Proposal only:
nothing here is approved or built. Grounded in the current code and the known-limitations
sections of docs/INTELLIGENCE_USER_GUIDE.md, OVERWATCH_USER_GUIDE.md and
THREAT_INTELLIGENCE_USER_GUIDE.md.

Standing constraints honored: free/key-less sources preferred (every endpoint verified live
before building), no social-media scraping, no Ticketmaster, light server-AI mode (droplet runs
only short summaries on a small model).

## ~~Media Watch (/admin/news-intelligence)~~ DONE 2026-10-06

Improve:
1. ~~Incremental refresh - merge by article URL instead of delete-and-reinsert; keep existing hot takes (fewer Ollama calls, stable order).~~
2. ~~Per-user read/unread state and a "new since your last visit" badge (today the badge is just a 24h count).~~
3. ~~Save/bookmark articles so they survive the hard 24-hour expiry; make retention configurable.~~
4. ~~Cluster the same story across outlets into one card ("covered by 5 outlets").~~
5. ~~Show feed and hot-take failures on the page (they fail silently); base "Breaking" on recency + multi-outlet velocity instead of keywords.~~

New:
1. ~~Custom RSS/Atom feeds per topic (any outlet, no key).~~
2. ~~Daily/weekly topic digest email (reuses the email delivery + weekly digest code).~~
3. ~~Automation trigger "new article matches topic" -> notifications/workflows.~~
4. ~~"Clip to Sentinel" - save an article with summary and source into a Sentinel page.~~
5. ~~Trends view - mentions per topic over time and rising keywords (needs history retained).~~

## ~~Civic Watch (/admin/government-intelligence)~~ DONE 2026-10-08

Improve:
1. ~~Configurable location (area is hard-coded to Kathleen / Houston County, GA).~~
2. ~~"Generate overview now" button, and extend SentinelGPT overviews to federal bills.~~
3. ~~Fix dead/missing local sources - Houston County calendar URL 404s; Visit Macon /events/ is server-rendered and unscraped.~~
4. ~~"What changed" highlighting since the last refresh (new votes, newly signed laws).~~
5. ~~Show how your own representatives voted (district lookup), not just highlight the GA delegation.~~

New:
1. ~~Bill watchlist - follow GA/federal bills, alert on status changes (email or automation).~~
2. ~~County commission agendas/minutes ingestion with short AI summaries.~~
3. ~~Federal Register rules and open comment periods matching keywords (key-less API).~~
4. ~~Upcoming elections + sample-ballot/polling info for the area (source must verify key-less).~~
5. ~~Grants.gov funding opportunities matching business keywords.~~

## ~~BI Dashboards (/admin/business-intelligence)~~ DONE 2026-10-06

Improve:
1. ~~Custom date range and period-over-period comparison.~~
2. ~~Real charting library (Chart.js via the already-allowed jsDelivr) - tooltips, lifts the top-12 cap.~~
3. ~~Drill-down - click a bar to see the underlying deals/articles/advertisers.~~
4. ~~Dashboard layout - reorder/resize widgets, KPI tiles, optional team-shared dashboards.~~
5. ~~Cache heavy queries with a real "data as of" time (today everything recomputes on every load).~~

New:
1. ~~More data sources - support tickets (volume/SLA/CSAT), email campaigns, Growth/social, form submissions, automation runs, Sentinel activity.~~
2. ~~Scheduled report emails (weekly).~~
3. ~~Goals/thresholds with progress lines and an automation trigger when crossed.~~
4. ~~CSV and image export per widget.~~
5. ~~"Ask about my data" - plain-English question -> picks source/metric/range (runs on local Ollama per light mode).~~

## Overwatch (/admin/osint)

Improve:
1. Finish the camera batch already researched - Singapore, Hong Kong, Iceland, MN/NE plow cams, Tennessee; Colorado's successor API.
2. Camera health - detect frozen/stale images (unchanged hash over several fetches) and flag or hide them.
3. Scale - server-side viewport-bounded camera queries as coverage grows.
4. Normalize incidents across WZDx/CARS511/others (severity, lanes, end time) with type filters.
5. Coverage index as a map layer plus per-source status (last success, camera count).

New:
1. Route watch - enter A -> B, show cameras, incidents and alerts along the route.
2. Geofenced alerts - saved areas trigger notifications on new incidents/severe weather.
3. Time-lapse of favorited cameras (periodic snapshots, storage-capped).
4. More key-less hazard layers - USGS earthquakes, NIFC/WFIGS wildfire perimeters, NWS river gauges (verify live first).
5. Snapshot analysis -> saved incident report in Sentinel (location, time, image, AI notes).

## ~~Threat Intelligence (/admin/threat-intel)~~ DONE 2026-10-06

Improve:
1. ~~All DNS record types (MX/TXT/NS/CAA) via key-less DNS-over-HTTPS - no new resolver library needed.~~
2. ~~RDAP via IANA's bootstrap file directly, not only rdap.org's redirect table.~~
3. ~~One search box that auto-detects IP / domain / URL / hash / CVE (hash checks the cached MalwareBazaar data).~~
4. ~~KEV/CVE filters by vendor/product, EPSS sort, "new since last visit".~~
5. ~~Investigation history - saved lookups, one-click export to Sentinel.~~

New:
1. ~~Asset watchlist - your own domains/IPs (grantwatson.dev, gwsapp.net, the droplet): cert expiry, new CT subdomains, blocklist hits, exposed ports via InternetDB.~~
2. ~~Stack -> CVE matching - list your software (.NET, Docker images, Ollama) and alert on new KEV / high-EPSS CVEs.~~
3. ~~Email security posture check for your domains - SPF, DKIM, DMARC, MTA-STS.~~
4. ~~Dependency advisories for this repo's NuGet/npm packages via OSV.dev (key-less).~~
5. ~~Daily threat digest email built from the existing automation nodes.~~

## Status

- 2026-10-05: Grant chose "one page's 10 items" -> Threat Intelligence first.
- Threat Intelligence: all 10 BUILT 2026-10-06 (improve 1-5 + new 1-5). New "My exposure" tab,
  ThreatMonitorService + ThreatMonitorBackgroundService, migration AddThreatIntelExposureMonitor.
  Daily digest is a dedicated service (not an automation-node workflow) so it can track which
  findings were already sent.
- 2026-10-06: Grant said "move on to the next unit of work" -> Media Watch (next in plan order).
  BUILT: merge-on-refresh (only new articles summarized), per-admin read/unread, Saved +
  configurable retention (1/2/3/7 days), story grouping across outlets, refresh issues shown per
  topic + "Breaking" = 3+ outlets in 6h, daily/weekly digest, "Media Watch Articles Found"
  automation trigger, Clip to Sentinel, Trends (14-day bars + rising words).
  "New 1: custom RSS feeds per topic" turned out to ALREADY EXIST (WatchedTopic.TrustedFeedUrls),
  so it was replaced with "Find a site's feed" (RSS/Atom auto-discovery) on the topic form.
- 2026-10-06: Grant: "order is moot, pick one and complete it, then the next" -> BI Dashboards,
  built ONE ITEM AT A TIME (budget-limited), each verified before the next. Progress:
  - Improve 1 DONE: dashboard-wide custom date range (BiDateRange, viewing-only) + PreviousTotal
    (equally long window before) shown as ▲/▼ %; totals now computed before the top-12 cap.
  - Improve 2 DONE: Chart.js 4.4.7 vendored at wwwroot/lib/chartjs (served from 'self'), drawn
    by wwwroot/js/biCharts.js; display cap 12 -> 50; tooltips.
  - New 4 DONE: per-widget Export CSV (in-page data: URI, formula-injection safe) + Export PNG.
  - Improve 5 DONE: IMemoryCache per report definition + window (5 min), BiChartResult.DataAsOf,
    "Refresh" button (refresh: true). Previews uncached.
  - Improve 3 DONE: DrillDownAsync (deals by stage/month, CJ records by advertiser, article
    views by day; max 200) opened by clicking a chart point (Chart.js onClick -> JSInvokable) or a
    table row. Browser-checked: Chart.js renders, comparison/range/refresh work (2026-10-06).
  - New 1 DONE (partial scope, by choice): Support tickets (count / SLA breaches / avg CSAT by
    status, priority, month), Form submissions (by form page, month), Automation runs (by
    status, workflow, month), each with drill-down. NOT added: email campaigns, Growth/social,
    Sentinel activity (Growth already has its own analytics page).
  - Improve 4 DONE (team-shared dashboards left out - optional in the plan): Move earlier/later,
    Full/Half width (IsWide), KPI tile visualization.
  - New 3 DONE: per-widget goal (GoalValue, floor or ceiling) with progress bar; hourly
    BiGoalBackgroundService -> EvaluateGoalsAsync (GoalLastMet, fires once per crossing) ->
    "bi.goalCrossedTrigger" automation trigger (AutomationWorkflow.TriggerBiGoalCrossed).
    Migration 20261006200631_AddBiWidgetLayoutAndGoals.
  - New 2 DONE: per-admin weekly report email (BiReportSubscriptions table, migration
    20261006201221_AddBiReportSubscriptions; BiReportEmailService; sent from the same hourly
    BiGoalBackgroundService tick; "Send now"). Uses Settings > Email (GrowthReportEmailOptions).
  - New 5 DONE: "Ask about my data" - browser-local Ollama (BrowserLocalOllamaService relay)
    picks a catalogued report via BiQuestionPlanner (validated; keyword fallback when Ollama is
    unreachable or answers invalidly); "Open in builder to pin".
  - BI Dashboards: ALL 10 DONE 2026-10-06. Next page: Civic Watch.
- 2026-10-07/08: Civic Watch - ALL 10 BUILT (new ICivicWatchService/CivicWatchService +
  CivicWatchBackgroundService; tables CivicWatchSettings, WatchedBills, CivicItemSightings,
  CivicMeetingDocuments via migration 20261007235525_AddCivicWatch). Notes per item:
  - Improve 1: home label/address/lat-lon in Settings; Census geocoder + district lookup. Feeds stay
    Houston County/Georgia (scope decision - other states' sources are not swappable).
  - Improve 2: "Generate overview now" in LegislationBriefPanel; overviews extended to federal roll calls.
  - Improve 3: /residents/ (403) -> newcomers.cms; Visit Macon added via Playwright (Algolia list
    renders only after scrolling). County calendar.cms actually answers 200 now.
  - Improve 4: CivicItemSightings (first run = baseline) -> "What changed" strip + New pills.
  - Improve 5: exact member-ID match (House name-id/bioguide, Senate lis_member_id via
    congress-legislators; GA members/list/{session}?chamber=1|2 ids).
  - New 1: watchlist (GA searchquery, congress.gov v3/bill) hourly + email + civic.billStatusChangedTrigger.
    congress.gov DEMO_KEY (~30 req/h/IP) is mostly used by the hearings fetch -> recommend a free
    api.data.gov key in CongressApi:ApiKey.
  - New 2: county /minutes/*.pdf agendas/minutes, PdfPig text, 2 summaries/hour (background priority).
  - New 3: Federal Register (relevance, 30 days, RULE/PRORULE/NOTICE). New 4: county election-dates
    table + Board of Elections docs (no key-less per-voter ballot source; links to My Voter Page).
  - New 5: Grants.gov search2.
- 2026-10-08: Overwatch started (Grant: "move on to the next unit of work"). One item at a time:
  - Improve 1 DONE: Iceland (Vegagerðin, 496), Hong Kong (TD, ~1,013; file has a DOUBLE UTF-16
    BOM), Singapore (LTA, list re-read every minute - image URLs rotate). Excluded after live
    checks: Tennessee (TDOT API needs the key embedded in SmartWay's app config), Colorado
    (carsprogram camera API still undocumented), MN plow cams (images 403), NE plow cams (dead since 2023).
  - Improve 2 DONE: CameraHealthService (server-side hash history; Frozen/Stale/Offline/Live),
    10-min favorites sweep (CameraHealthBackgroundService), stream-panel badge, HEALTHY ONLY toggle.
