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

## Phase 2: reading, analysis and outputs

| # | Item |
| --- | --- |
| 2.1 | Story timelines (every development in order) |
| 2.2 | What changed since you last looked |
| 2.3 | Morning and evening briefings (to Sentinel, optionally Mail) |
| 2.4 | Framing comparison panel |
| 2.5 | Entity pages (people, organisations, places) |
| 2.6 | Quote tracker |
| 2.7 | Full-article translation |
| 2.8 | Saved searches as feeds (with RSS) |
| 2.9 | Business impact (CRM companies, contacts, brands) |
| 2.10 | Ask World News (answers only from coverage, with citations) |
| 2.11 | Reader mode |
| 2.12 | Listen to the news (audio on the Mac) |
| 2.13 | Reading list |
| 2.14 | Bias-balanced view |
| 2.15 | Trend charts |
| 2.16 | Sentiment and tone over time |
| 2.17 | Prediction tracking |
| 2.18 | Publish a curated digest (article/newsletter) |
| 2.19 | Shareable story pages (token, password, expiry, revoke) |
| 2.20 | Export for research (CSV, JSON, PDF dossier) |
| 2.21 | Automation triggers (World News events start workflows) |
| (done) | Map playback: already built 09-11-26 (world-news-event-timeline-2026-11-09.md) |

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
