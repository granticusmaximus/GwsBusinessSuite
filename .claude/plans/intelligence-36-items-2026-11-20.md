# Media Watch, World News, Civic Watch: 18 improvements + 18 features (20-11-26)

Grant asked for 6 new features and 6 improvements per area, then: "Break this into 2 phases and
complete those two phases". Phase 1 is the improvements (existing code), Phase 2 the new features.
Key-less sources only; AI through `IOllamaService`; personal Sentinel pages via
`SentinelPersonalPages`; dates dd-MM-yy, 24h.

## Phase 1: improvements

Media Watch
- [x] MI1 One article, one link: canonical URL from the article page replaces redirect links
- [x] MI2 Group stories by meaning (shared helper with World News style token similarity + places)
- [x] MI3 AI takes in the background: "take pending", no warning for a busy model, background fill
- [x] MI4 Reader fallback to the crawler's kept text
- [x] MI5 Digest email uses best-reads ranking, mutes, and short queued reads
- [x] MI6 Trends: click a rising word to filter; 14/30/60 days

World News
- [x] WI1 Disagreements claim by claim in the analysis
- [x] WI2 Summaries for every story with 2+ outlets
- [x] WI3 Merge suggestions (the reverse of the hourly split)
- [x] WI4 Story page in tabs (Summary, Coverage, Trust, Evidence)
- [x] WI5 Personal Today: pinned countries and kinds lead
- [x] WI6 Read a report translated (reader + model)

Civic Watch
- [x] CI1 Settings per person (location, keywords, alert email; shared as the default)
- [x] CI2 Georgia bills by keyword (General Assembly API)
- [x] CI3 Overviews for Community desk items; guide no longer says Mac-only
- [x] CI4 What changed since your last visit
- [x] CI5 congress.gov DEMO_KEY budget: reserve room for bill adds
- [x] CI6 Search the meeting papers; mention alerts; summary cap lifted with server AI

## Phase 2: new features

Media Watch
- [x] MF1 Listen mode (speech in the reader and the queue)
- [x] MF2 Release radar (GitHub releases.atom for the repo's packages)
- [x] MF3 Ask the article
- [x] MF4 Search your library (read, queued, clipped)
- [x] MF5 Follow an author
- [x] MF6 Old highlights resurface on the weekly page

World News
- [x] WF1 Catch me up on a Developing story
- [x] WF2 Morning world briefing page in Sentinel
- [x] WF3 People and organisation pages (Wikidata)
- [x] WF4 Front pages compared by country
- [x] WF5 Coverage spread replay (hour by hour, by country)
- [x] WF6 Watchlist dossiers in Sentinel

Civic Watch
- [x] CF1 Calendar subscription (.ics)
- [x] CF2 Follow an agenda item across meetings
- [x] CF3 School board desk
- [x] CF4 Weekly civic page in Sentinel
- [x] CF5 Representative scorecards
- [x] CF6 Public comment helper

## Progress

- 20-11-26: plan written; Phase 1 started.
- 20-11-26: Phase 1 built and unit-tested (MediaWatchImprovementsTests, WorldNewsImprovementsTests,
  CivicWatchTests additions). Migration AddCivicWatchPersonal (CivicWatchSettings.Username/MentionTerms,
  CivicWatchUserStates, CivicMeetingDocuments.Text). World News store adds analyses.disputes and
  today_pins in place (ALTER/CREATE IF NOT EXISTS). Notes:
  - MI2 embeds headlines on feed load (embeddinggemma, 48 a load, 4 s wait, cached 2 days).
  - MI3 background fill every 5 min in NewsRefreshBackgroundService; busy model = no warning.
  - WI2 summaries: SummariesPerRead 4, SummaryMinOutlets 2 (config keys).
  - WI3 hourly AI check of up to 3 close calls; confirmed pairs merged ("automatic (hourly merge check)").
  - WI4 story tabs Summary/Coverage/Trust/Desk; jump-link script removed from app.js.
  - CI5 CongressApiBudget: 45/day on DEMO_KEY, 10 kept for adding bills; federal bills re-checked 6-hourly.
- 20-11-26: Phase 1 gate run found 2 failures (btn-close-sm class, an issue-text test); both fixed.
- 20-11-26: Phase 2 built and unit-tested (IntelligenceFeaturesTests; MediaWatchTests and CivicWatchTests
  additions). Migrations AddMediaWatchAuthors (NewsItems.Author, FollowedNewsAuthors) and
  AddCivicWatchFeatures (CivicWatchUserStates.CalendarToken/FollowedItems/WeeklyPageBuiltAt,
  CivicMemberVotes). World News store adds briefing_subscribers in place. Notes:
  - MF1 listen = browser speechSynthesis (gwsReader.speak); MF2 = new Feeds topic type + ReleaseRadar
    (a test checks each package is referenced); MF3 AskAsync drops quotes not in the article.
  - WF2/WF6 pages written hourly from the read (WritePersonalPagesIfDueAsync) via
    SentinelPersonalPages.WriteAsync; WF3 /admin/world-news/about (Wikidata wbsearchentities);
    WF4 /admin/world-news/front-pages; WF5 "How it spread" on the Coverage tab.
  - CF1 /civic/calendar/{token}.ics (anonymous, rate-limited, like the booking feed); CF3 reads the
    district's Simbli listing (agenda text loads by script, so meetings + links only);
    CF5 keeps GA delegation + GA General Assembly votes; CF6 drafts only, filing stays on federalregister.gov.
- 20-11-26: release gate PARTIAL (pass) with both phases. Visual check started and stopped early at Grant's
  request (near the session limit): the World News About page and More menu showed with no console
  errors; the rest wasn't checked on screen. The local World News copy had no grouped stories, so
  tabs, spread and front pages need an on-screen look against real data. Walkthrough script:
  scratchpad visual/pw3/walk.cs (port 5299, DB copy, localdiag login).
