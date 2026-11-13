# World News: trust, crawler, reading and outputs (13-11-26)

Grant asked (13-11-26) for every World News / crawler idea proposed on 12-11-26 and 13-11-26
(verification, crawling, and three lists of 15) to be split into two phases and built.

Ground rules (AGENTS.md): sources are free and key-less; no social-network scraping; respect a
site's refusal (RefusedHostPause, robots.txt); heavy AI runs on an Admin's Mac through
`MacRelayScope.ForSharedWork()` (news is public data); the articles table is ~2 GB on the droplet,
so every new query is indexed and bounded. Build on `WorldNewsStore` (worldnews.db) and
`WorldNewsKit`; no second news store.

Already built before this plan (not rebuilt): Overwatch replay of World News (map playback,
09-11-26), per-story watches and alerts, surge alerts, story read tracking, article excerpts,
headline translation, the story document's compare/contrast (EventAnalyzer), disputed/corroborated
figures (CoverageClaims), state-media tags, Wikipedia most-read.

## Phase 1: trust and the crawler

| # | Item | Notes |
| --- | --- | --- |
| 1.1 | Spam and duplicate filter | Press-release wires, content farms and SEO listicles kept out of stories and counts. |
| 1.2 | Independent sources | Wire copies (Reuters/AP/AFP credits, near-identical text) and same-owner outlets count once: "26 outlets · 4 independent". |
| 1.3 | Confidence label | Confirmed (primary source, or 3+ independent) / Reported / Single source / Disputed. |
| 1.4 | Breaking-news gate for alerts | Surge and watch alerts wait for 2 independent sources or a primary source. |
| 1.5 | Conditional and adaptive polling | ETag/Last-Modified per feed; quiet feeds back off, surging stories speed up. |
| 1.6 | News sitemaps | Outlets' 48-hour news sitemaps as a source type beside RSS. |
| 1.7 | Fact-check feeds | AFP Fact Check, PolitiFact, Full Fact, Snopes; a fact-check in a story is shown and can mark it Disputed. |
| 1.8 | Corrections and edits | A report whose headline changes after it was first seen is recorded; "Correction:"/"Update:" noticed; first report credited. |
| 1.9 | Link-rot protection | The first-seen headline/description/excerpt kept; edited or vanished reports shown as such. |
| 1.10 | Full-text archive and search | FTS5 over headlines, descriptions and excerpts; phrase and boolean search. |
| 1.11 | Watchlists | Follow a topic, place, person or company; alerts (gated by 1.4) through the bell and push. |
| 1.12 | Unusual silence | A country whose coverage falls far below its normal level is flagged (links Overwatch outages). |
| 1.13 | Primary-source corroboration | Stories matched by kind, place and time with USGS/GDACS hazards and WHO notices already collected. |
| 1.14 | Follow a surging story | A surge triggers a targeted GDELT search to fill its coverage across languages. |
| 1.15 | Source manager | Every feed/sitemap with health, freshness and pause state; add an outlet by homepage (autodiscovery). |
| 1.16 | Crawl dashboard | Reports per hour, new stories, failures, freshness by country. |
| 1.17 | Coverage-gap filling | Countries with thin coverage shown; regional outlets added through 1.15. |
| 1.18 | Outlet track records | First-to-report, corrections and disputed figures per outlet, built from evidence. |
| 1.19 | Wikipedia as an early signal | The Current events portal read as a signal beside most-read. |

## Phase 2 replaced 13-11-26: two phases toward news that is transparent, measured and checkable

Grant's goal: news the app can stand behind. "Unbiased" can't honestly be claimed; what can be is
every claim showing its evidence, the system's own blind spots measured and published, and labels
whose accuracy is tracked. Old Phase 2's reading items move to Phase B, built on top.

### Phase A: accountable evidence

| # | Item |
| --- | --- |
| A1 | Claims with evidence chains: each story's figures and attributed statements, who said them first, who repeated them, and what backs or contradicts them. |
| A2 | Who cited whom: "told Reuters", "according to the ministry", "said in a statement" in descriptions and excerpts trace a report to its real origin, which independence then counts. |
| A3 | Ownership from Wikidata: outlets' owners, parent companies and state funding, imported weekly (free SPARQL), on top of the hand list. |
| A4 | Rumour clock: a story still on one independent source after 12 hours says "Unconfirmed after 12 h". |
| A5 | Coverage balance, measured: reports by region, language and ownership type against where stories happen, and how often a story about a place is led by an outlet from there. |
| A6 | Recall against official records: of USGS M5+ earthquakes, GDACS alerts and WHO notices, how many World News surfaced and how fast. |
| A7 | Calibration with versioned rules: each label is stamped with the rules version and checked again later; hit rate per label (held up, corrected, disputed) is published. |
| A8 | Tamper-evident record: label snapshots hash-chained, with an integrity check. |
| A9 | Corrections and challenges: anyone can challenge a label with evidence; outcomes and a corrections log stay attached. |
| A10 | Methodology page: the rules (versioned), balance, recall, calibration and corrections, in one place. |

### Phase B: the evidence board and reading

| # | Item |
| --- | --- |
| B1 | Evidence board on Overwatch: a story's coverage, official records and sensor signals (outages, fires, seismic, airspace, ships, cameras) on one map and timeline. |
| B2 | Physical claims checked against sensors (outage, airport, fire claims: supported / contradicted / no data). |
| B3 | Recycled-photo detection (image fingerprints against the archive; Content Credentials where present). |
| B4 | Stealth edits to article bodies (first excerpt kept, later reads diffed). |
| B5 | Fake-network detection (shared owners/hosting/analytics IDs, near-identical text, new domains via RDAP). |
| B6 | Gold set and red-team runs: past stories with known outcomes, run before every rules change. |
| B7 | AI summaries checked against their sources; unsupported sentences flagged. |
| B8 | GDELT 15-minute global feed and local-first weighting (proximity of outlet to event). |
| B9 | Official newsrooms as sources (government, UN, ReliefWeb RSS). |
| B10 | Reading on top: story timelines, briefings, entity pages, quote tracker, full translation, saved-search RSS, business impact, Ask World News, reader mode, listen, reading list, balanced view, trends, tone, predictions, digest, shareable pages, export, automation triggers. |

## Progress

- 13-11-26: plan written; Phase 1 started. Found already built and marked: 2.2 "what changed since
  you last read it" (StoryChangesFinder), first report credited, map playback.
- 13-11-26 Phase 1 done so far (tests in WorldNewsTrustTests / WorldNewsCrawlerTests):
  - 1.1 NewsNoise (dropped in WorldNewsStore.AddArticles). 1.2 SourceIndependence (wire credits,
    copy keys, owner groups; fact-checkers excluded). 1.3 StoryConfidenceRules + story-page trust
    panel (WorldNewsStoryView). 1.4 gate: WorldNewsService.MayAlert on surge and watch alerts.
  - 1.5 FeedSchedule + feed_health table; pipeline reads only due feeds with ETag/Last-Modified.
  - 1.6 news sitemaps (Guardian, BBC index, Al Jazeera, DW; AP/Reuters/AFP refuse the app).
  - 1.7 fact-check feeds (Snopes, FactCheck.org, PolitiFact, Lead Stories, Full Fact) + verdicts.
  - 1.8/1.9 article_edits: changed headlines recorded, first-seen headline kept, shown on story page.
  - 1.10 FTS5 articles_fts (triggers + EnsureFullTextIndex off startup; FullTextQuery) behind the
    existing "Search every report" box.
  - 1.11 keyword_watches + CreateKeywordAlerts (gated, once per story) + watchlist panel.
  - 1.12 country_hours counts + CoverageSilenceRules + "Unusually quiet" line on World News.
  - 1.13 PrimarySources (WHO in reports; USGS/GDACS/NHC/NASA via HazardLayerService) through
    WorldNewsService.ConfirmWithPrimarySourcesAsync after the story page shows.
  - 1.14 FollowUpQueries: last read's surging stories searched on GDELT (max 3 a read).
  - Release gate passed after 1.11 (checkpoint).
  - 1.15-1.18 /admin/world-news/sources (WorldNewsSources.razor): dashboard numbers, coverage by
    region (gaps), every feed with state + pause/resume, add an outlet by homepage
    (FeedDiscovery + DiscoverFeedsAsync, validated, refusals reported), outlet records
    (outlet_days counts, first reports, headline changes, corrections).
  - 1.19 Wikipedia Current events portal (WikipediaCurrentEvents), conditional, a signal not a source.
  - Independence counts one origin per outlet (live check found 66 sources for 65 outlets before).
  - Checked live on a local copy (story trust panel, sources page, Guardian discovery). Gate passed.
  - Known limit: Google News reports carry no description or wire credit, so wire copies that only
    arrive through Google News aren't detected; those stories can read as more independent than they are.
- PHASE 1 DONE 13-11-26. Next: Phase 2 (2.1, 2.3-2.21; 2.2 and map playback were already built).
- 13-11-26: Grant asked for news the app can stand behind; Phase 2 replaced by Phases A and B above. Phase A started.
- 13-11-26 PHASE A DONE (tests in WorldNewsTrustTests; gate passed; checked live on a local copy):
  - A1/A2: CitedSources ("according to", "said in a statement") on the trust panel; wire credits now
    include "told/citing/according to/reported by Reuters|AP|AFP" and "Reuters reported".
    A1 is figures (CoverageClaims, existing) + cited sources per story; claim-level evidence chains
    with sensor support are B1/B2.
  - A3: Wikidata owners (current statements only, matched by website domain to the outlet's feeds),
    weekly, outlet_owners table, OwnershipGraph groups shared owners; SourceIndependence.ImportedOwners.
  - A4: rumour clock in StoryConfidenceRules (12 h on one source -> "unconfirmed").
  - A5/A6: CoverageBalance + Recall (USGS M5.5+ week feed) in WorldNewsService.TrustReportAsync (hourly cache).
  - A7/A8: label_snapshots hash chain (SnapshotLabels hourly during reads, only on change, never
    pruned), Calibration.Score, LabelLedger.FirstBroken; RulesVersion stamped on every label.
  - A9: label_challenges (story page form; uphold/reject on the methodology page = corrections log).
  - A10: /admin/world-news/methodology (rules, changelog, calibration, recall, balance, record, challenges).
  - Calibration needs a day of snapshots and balance a week of counts before they say much.
- Next: Phase B.

