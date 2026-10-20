# World news situations in the Mac app (researched 20-10-26)

Grant asked (20-10-26) for a map of what clstr.news does: reading news from across the world,
combining the sources on one story into a single document, and summarizing it. It is to be built
as a Mac app feature. Grant then asked for it to be built (20-10-26).

## Status

**Decided 20-10-26 (Claude, as the default; Grant can redirect):** Mac-only. Reading, embedding,
grouping and summaries all run in the Mac app against the local Ollama, with no server changes.
Coverage is every country the sources reach.

**Phase 1 done 20-10-26: reading + events.** New library `src/GwsBusinessSuite.WorldNewsKit`
(in both solutions; tests reference it; the Mac app doesn't yet).
- `GoogleNewsFeed`: 41 country editions in their own languages. All 41 answered live, about
  6,000 reports per pass. Each story's related coverage shares a `GroupHint`.
- `GdeltDocClient`: one paced gate (6 s apart), a 15-minute pause after HTTP 429, and at most a
  day of lookback. **Off by default** (`GdeltQueries` is empty): from this network GDELT answered
  only 1 request in 5 with data on 20-10-26, even single calls 45 minutes apart.
- `WorldNewsStore` (SQLite), `OllamaClient.EmbedAsync` (`/api/embed`, batched),
  `EventClusterer`, and `WorldNewsPipeline.RunCycleAsync`.
- **Grouping, tuned on about 5,800 live reports:**
  - Join an event at centroid similarity 0.65; feed hints get 0.1 of slack.
  - Twin events at 0.85 or above are merged.
  - The comment in `EventClusterer` records why each number was chosen.
- **Live result:** Japan's Mars moon mission was one event with 80 outlets in 15 languages,
  and Alberta's referendum one event in 9 languages. "Trump on a Putin-Xi summit" and "Trump on
  sanctions waivers" stayed separate events.
- **Timing:** the first pass takes 80 s, almost all of it embedding. A pass with nothing new
  takes 4 s.

**Found 20-10-26, changes the later phases:** Google News links are Google redirects. Turning
one into the outlet's URL needs Google's undocumented batchexecute call, which is fragile and
not used here. So article text comes from outlets' own RSS feeds instead: real URLs plus a short
summary of each story. Next phase: add a curated multilingual outlet list (Al Jazeera, DW,
France 24, BBC language services, Euronews, RFI, NHK, Yonhap and others) and build summaries
from headlines + those snippets.

**Phase 2 done 20-10-26: the Mac "World" tab.**
- `WorldNewsPage` is a Mac-only tab (added in `AppShell.xaml.cs` under `MACCATALYST`) with a
  globe toolbar button (`toolbar-worldnews.png`).
- Left: events, busiest first, with a filter, range (24 h / 3 d / 7 d) and Refresh. Right: the
  event's reports grouped by language, built by `WorldNewsHtml`; links open in the browser.
- `WorldNewsService` runs the pipeline every 15 minutes once the tab has been opened. The
  database lives at `AppDataDirectory/worldnews/world.db`.
- Builds for Mac Catalyst and the iOS simulator succeed. **Not run on screen:** the debug build
  shares the installed app's bundle id and data folder, so it wasn't launched. Grant to open
  the World tab after installing.

**Moved to the whole app 20-10-26 (Grant: "part of the whole application ... add it to the Intel
section").** The Mac World tab is gone; the feature is now the web page **Intelligence > World
News** (`/admin/world-news`, `WorldNews.razor`). The Mac app shows it in its Workspace tab.
- **Server reading:** `Infrastructure/Services/WorldNewsService` + `WorldNewsBackgroundService`
  run the same WorldNewsKit pipeline every 15 minutes (first read 60 s after start-up), with
  embeddinggemma on the server's Ollama (already in `ollama/server-models.txt`; Light mode
  allows it, since it's embeddings, not generation).
- **Embedding cap:** `WorldNews:MaxEmbedPerCycle` (default 1,500) spreads the first ~6,000-report
  backlog over several reads, so chat on the droplet stays responsive.
- **Storage:** its own SQLite file, `WorldNews:DatabasePath` (default `/app/data/worldnews.db`,
  `worldnews.db` in Development). No migrations, 30 days kept. `WorldNews:Enabled` turns
  reading off.
- **Checked on a throwaway local server:** the background reader alone read 5,834 reports into
  2,645 events. The responsive audit (phone-390, laptop-1280, desktop-1920, plus an "event"
  state that picks the first event) is clean after fixing an overlapping list and small report
  links. The server's live performance on the droplet is not yet seen.
- **Kept from the Mac work:** `MacMenuPickerHandler` (it fixes every Mac picker).
  `WorldNewsHtml` was removed; the web page renders its own markup.

**Revised order:**
2. ~~The Mac tab~~ The web page (done, above).
3. Outlet RSS feeds + the combined document + summaries.
4. Situations + claims.
5. Monitoring, the World view, and the SentinelGPT tool.

## What clstr.news does (checked live 20-10-26)

- **Volume:** about 700,000 articles a week from a commercial news-data provider (its ChatGPT
  listing says 100k+ a day from 40k+ sources). It runs 30 to 90 minutes behind the wires by
  design, so it suits tracking a story rather than breaking news.
- **Cluster = one event:** every article on the same event, across outlets and languages, merged
  into one item with every source linked.
- **Situation = a storyline:** clusters on the same developing story over days or weeks, linked
  together. Each situation has a status (ACTIVE or QUIET), a category, country codes, counts
  ("5 clusters · 88 sources · 5 days"), first seen and last updated times, and an internal
  significance score from 1 to 10.
- **Situation page sections, in order:**
  - **Overview:** one prose summary in its own words, revised as coverage arrives ("updated 13
    times", with revision history).
  - **Entities:** people, places and organizations.
  - **Claims:** "Coverage disagrees" pairs, each claim with its sources, e.g. "different death
    tolls (6 versus 5)". It also marks DISPUTED claims and consensus counts ("● 8 SOURCES"),
    and says it reports disagreement without judging it.
  - **Timeline:** one entry per cluster, newest first, each with a category and source count.
  - **Sources:** a list of outlet domains.
- **Feed:** time range (24h, 7d, 30d), sort (relevance, recent, most sources), country filter,
  categories (politics, business, technology, health, sports, culture, international, crime),
  and semantic search. Pro adds saved topics, several countries at once, and email alerts when
  a monitored situation develops.
- **World page:** events and countries in the last 3 hours, a most-mentioned-countries ranking,
  each country's top 3 stories, and the 10 biggest stories, each tagged with its situation.
- **Principles:** it reports what sources say and doesn't judge truth. It summarizes rather than
  republishing article text, and links every original.
- **API/MCP:** `api.clstr.news/v1`, with `/situations`, `/situations/{id}` (with timeline),
  `/clusters/{id}` (with articles) and `/search`. Free tier: 100 requests a day; Builder $29/mo.

## What we already have

- **News Intelligence (web):** `NewsIntelligenceService` and `NewsWatchService` pull Google News
  RSS plus curated outlet feeds. `NewsStoryClusterer` groups headlines by word overlap (≥ 50%),
  which only works within one language. Each item gets an Ollama summary on its own.
- **Mac app:** local Ollama, reached natively through `LocalOllamaBridge`. Installed models
  include `embeddinggemma` (multilingual embeddings) and `gemma4` / `qwen3:14b` for long
  summaries. `OfflineStore` keeps files. There's no SQLite and no notifications yet.
- **Server:** runs only the light `llama3.2`; heavy AI work is Mac-only (same rule as article
  generation).

## Sources (checked live 20-10-26)

- **GDELT DOC 2.0 API** (free, no key): an English query returns matching articles in other
  languages. "istanbul flood" returned Turkish, Russian, Bulgarian and German coverage, each with
  `url`, `title`, `seendate`, `domain`, `language` and `sourcecountry`. Limit: **one request every
  5 seconds**. Two quick calls got HTTP 429 and a short lockout, so it needs a single paced queue.
  Up to 250 articles per call; updated every 15 minutes; `sourcecountry:` and `sourcelang:`
  filters.
- **Google News RSS country editions** (`hl`/`gl`/`ceid`): already used here, so adding
  editions gives local-language front pages for each country.
- **Existing curated outlet feeds.**
- clstr's own API could be a source too, but the aim is to build this ourselves, and its free
  tier is too small to depend on.

## How it works here

1. **Ingest and normalize:** canonical URL, title, outlet, language, country and time. Drop
   exact URL repeats and syndicated wire copies (same title from different domains).
2. **Article text:** fetch each page and pull out the main text (a Readability port such as
   SmartReader). Skip paywalled pages and respect robots.txt. Text is stored only for processing;
   only our summaries and the links are ever shown.
3. **Events (clusters):** embed title + lead with `embeddinggemma`. Because the vectors are
   multilingual, Turkish and German reports of one flood land together without translating
   first. Add each article to the nearest event from the last 48 hours if similar enough, else
   start a new event. Tune the threshold on a labelled sample, with the Istanbul floods as the
   first test case. Vectors are stored as blobs in SQLite and compared brute-force: a few
   thousand live events is small.
4. **Situations:** link events whose centroids are close (a lower threshold) and share entities
   or countries within 30 days. Ask the model only when the score is borderline. A situation
   goes QUIET after 48 hours with no new event.
5. **Combined document (the "single document"):** one per event and per situation.
   - A header: title, countries, languages, time span, outlet count.
   - Then each source in time order: outlet, country, language, time, link, and its key
     paragraphs, with repeated wire paragraphs collapsed.
   - It can be exported as Markdown or PDF, and it's what the summarizer reads.
6. **Summary:** map-reduce, so large stories fit the model's context.
   - Pull dated, numbered facts and claims from each source.
   - Merge them into: Overview; What's widely reported (with source counts); Where coverage
     disagrees (claim vs claim, each linked); What's new since the last version; Timeline;
     Entities.
   - Re-summarize only when new events arrive, and keep the revisions.
7. **World view:** events per country in the last 3 hours / 24 hours, most-mentioned countries,
   each country's top stories.
8. **Monitor:** watch a situation and get a macOS notification when it develops. SentinelGPT
   gets a `search_world_news` tool (semantic search over events and situations).

## Mac app UI (a native tab beside SentinelGPT, Notes and Capture)

- **Left:** time range, category, country, sort, search, and monitored situations.
- **Middle:** situation cards with status, counts, a "Latest:" line and last updated.
- **Right:** the situation, with Overview, Claims, Timeline, Sources and Document tabs (Document
  being the combined document, with export).
- **World:** a mode with the per-country view.
- **Keyboard:** R refreshes and ⌘F searches. Dates dd-MM-yy and times HH:mm.

## Phases

1. Ingestion v2: a paced GDELT queue, Google News country editions, normalized fields, dedupe.
2. OllamaKit `/api/embed` + event clustering + threshold tuning on a labelled sample.
3. Text extraction + the combined document + export.
4. Summaries: overview, consensus and disputed claims, timeline, attribution, revisions.
5. Situations: linking events over days, ACTIVE/QUIET, a significance score.
6. The Mac tab, World view, monitoring with notifications, and the SentinelGPT tool.

Each phase is a finished, verified unit (`./scripts/verify-release.sh`).

## Risks

- **GDELT pacing and outages:** Google News editions and outlet feeds keep it working without
  GDELT.
- **Clustering quality:** two wrong ways to fail, merging separate events or splitting one event.
  Measure on labelled samples before building on top.
- **Mac compute:** embeddings are cheap. Long summaries take tens of seconds each, so summarize
  the top situations first and only when they change.
- **Mac asleep:** if ingestion runs on the Mac, coverage stops while it sleeps (see decision 1).
- **Copyright:** summaries in our own words with links, never republished article text, which
  is clstr's stance too.

## Decisions for Grant

1. **Where ingestion runs.** Recommended: on the server, 24/7 (it already polls news and is
   cheap), with the Mac pulling new articles and doing the embeddings, clustering and summaries
   locally. The alternative is Mac-only, which is simpler but has gaps while the Mac sleeps.
2. **Mac only, or also on the web?** Should situations also appear in News Intelligence on the
   web, synced up from the Mac?
3. **How wide:** every country GDELT covers, or a chosen list of countries and topics.
