# World News and Overwatch: the next round (06-11-26)

Grant, on the proposal (World News features 1-6, World News UI, Overwatch features 1-4,
Overwatch UI): "I like this entire list. Proceed with this".

Each phase is built in the scratch worktree, tested, checked in a browser, then applied to main.
Then verify-release runs and Grant gets a commit message: he commits and pushes.

Found before planning (grep first):
- Watched areas and area alerts already exist (`OverwatchWatchArea`, `OverwatchAreaService`):
  traffic incidents, NWS weather, hazards and news. Phase 4d extends them; it isn't new.
- Production, last 7 days: most publishers come only through Google News. Busiest by events:
  - Yahoo, BBC Mundo, Infobae, The Guardian, Aftonbladet, ANSA, the New York Times, El País.
  - Dagens Nyheter, WELT, YNET, Yonhap, n-tv, G1, De Telegraaf, Le Figaro, La Nación.
  - Al Arabiya, Clarín, The Independent, Expressen, Chosun.

## Phase 1 - the story document, deeper - DONE (06/07-11-26)

Done:
- `StoryDocument` (WorldNewsKit) is the one record the document is drawn from.
  `WorldNewsService.Story(eventId, username)` builds it.
- 1a: the grid, as a pipe table in the dialog (AssistantMarkdown renders pipe tables) and in the
  PDF (MarkdownPdf draws tables).
- 1d: headlines translated after each analysis (`HeadlineTranslator`, `article_translations`).
- 1e: "what changed" (`StoryChangesFinder`, `analysis_history`, `story_reads`), marked read when
  the dialog opens.
- 1b and 1c (07-11-26): `WorldNewsStoryView.razor` draws `StoryDocument` (the dialog no longer
  renders the Markdown; the PDF still does). It owns the analysis bar, analyse-when-due, Analyse
  again and marking the story read, and fetches the story again at each analysis step, so the
  analysis shows before the translations finish. `WorldNewsStory.razor` is the page
  (`/admin/world-news/events/{id}`); the dialog has "Open as page" and Overwatch's READ DOCUMENT
  goes to the page. Chips: `StoryFilter` (WorldNewsKit). Checked locally in WebKit on a copy of
  the database with the local model (Serbia election story: 31 outlets, 9 languages).

- 1a **Who reports what:** a grid, rows the outlets and columns the agreed facts, a tick or a gap.
  It goes on the page, in the dialog and in the PDF (MarkdownPdf learns pipe tables).
- 1b **A story page**, `/admin/world-news/events/{id}`, beside the dialog. It can be linked to and
  bookmarked, and Back works. Overwatch's READ DOCUMENT opens it, and the dialog gets "Open as page".
- 1c **Region and language chips** on the story page, filtering the grid and the sources (for
  example, only outlets outside Western Europe and North America).
- 1d **English headlines:** the analysis also translates its sources' headlines (one per
  outlet, not English, up to 80), kept per article and shown under each original.
- 1e **What changed since you last read:** analyses are kept as versions; each reader's last-read
  version is remembered. On the next open, the top of the document shows:
  - outlets new since then;
  - facts added or dropped;
  - a claim that changed;
  - figures that moved.

## Phase 2 - World News behind the scenes
- 2a **Analyse ahead of time:** while an Admin Mac is online, the top 20 stories of the last day
  and the latest event of every watched situation are analysed when due, a few each round, in
  the shared (lower-priority) lane.
- 2b **Background translations** of those stories' headlines.
- 2c **More outlets' own feeds:** the busiest Google-News-only publishers above, each feed fetched
  and checked before it's added. Refusals are respected.
- 2d **Fact-checks:**
  - Public fact-check feeds from IFCN signatories (each checked first).
  - Matched to a story by the words of its claim and headline.
  - A "Fact-checks" section in the document when there's a match.

## Phase 3 - the World News list
- **Story cards:**
  - English headline and a one-line claim.
  - Outlet counts by language.
  - A "figures disputed" flag.
  - A coverage sparkline (reports per hour, the T5 Sparkline).
- **Region and language chips** filtering the list.

## Phase 4 - Overwatch features
- 4a **What the sensors say:** for a news story, what other layers show near that place and time:
  - quakes and GDACS hazards, internet outages, airport delays;
  - NWS alerts, power outages, convergence;
  - in the World News panel, next to what the outlets say.
- 4b **Layer presets:** Weather, Conflict, Cyber, Infrastructure.
- 4c **One time slider:** the last 24, 48 or 72 hours, for every layer whose items carry a time.
  Layers with no history say they're live only.
- 4d **Watched areas also alert on** internet outages, airport closures, news surges and aircraft
  emergencies (7500/7600/7700). New alerts reach the Mac app as notifications.

## Phase 5 - Overwatch UI
- 5a **One detail panel shared by every layer,** with the same actions: Read document (news), Ask
  SentinelGPT (with that item as context), Capture.
- 5b **The layer panel:** search, active layers as chips at the top, a count per layer, a legend.
- 5c **Clustering** of markers when zoomed out; panels as a bottom sheet on phones.

## Phase 6 - Key-less sources (07-11-26)

Grant: "Let's actually find routes where I do not need a key from someone", then "GO" on the
list. Every route was checked live on 07-11-26 with the app's own client; refusals are left out.
6a goes first and lands on its own; 6b-6f (Overwatch) are built in the scratch worktree and land
together; 6g last.

- 6a **US outlets' own feeds, then global ones** (World News):
  - US: NYT (World and US), Washington Post, WSJ, CBS, NBC, ABC (US), Fox, PBS News, Politico,
    The Hill, Axios, LA Times, Bloomberg, CS Monitor, ProPublica, NY Post, CNBC. Not CNN (its
    feed stopped in April 2023) or USA Today (HTTP 402).
  - Global: Infobae, G1, The Globe and Mail, Africanews, The Moscow Times, CGTN. Not Al Arabiya
    or Arab News (403 to the app; Arab News answered curl), Kyodo or Mail & Guardian (404).
  - Official: UN News, and WHO's disease outbreak news (its JSON API, newest first).
  - One name per outlet (`OutletNames`): Google News's site-title and short names ("ABC News -
    Breaking News, Latest News and Videos", "WSJ", "Bloomberg.com") map to the outlet's own, and
    the two ABCs become "ABC News (US)" and "ABC News (Australia)".
  - "State media" on state-run outlets (TASS, Xinhua, CGTN, Anadolu...) in the coverage list and
    the PDF. Public broadcasters with independent editors (BBC, NHK, DW) aren't labelled.
- 6b **Travel advice** (Overwatch): the US State Department's level for each of 218 countries as
  country shading.
- 6c **More hazards** (Overwatch, `HazardLayerService`): NOAA hurricane center storms (one per
  storm with GDACS), and NASA EONET kinds nothing else covers (landslides, dust, sea ice).
- 6d **Fire hotspots** (Overwatch): NASA FIRMS's public 24-hour files (NOAA-20 VIIRS, MODIS),
  grouped into cells so the globe isn't drawing 100,000 points.
- 6e **Conflict** (Overwatch): GDELT's 15-minute event files as "Reported" (material conflict
  described by several articles) and UCDP's candidate events as "Confirmed" (monthly, with deaths).
- 6f **Humanitarian** (Overwatch): ReliefWeb's disasters feed and IFRC GO's emergencies, at the
  country.
- 6g **World News signals:** Wikipedia's most-read pages matched to stories ("Most read on
  Wikipedia"), and a GDELT search trial with its answer rate logged.
