# Overwatch world signals, and SentinelGPT reach (05-11-26)

Grant asked what Overwatch could take from six repos: globalhackmap, worldmonitor,
Awesome-OSINT-List, sherlock, Digital-Forensics-Guide and firecrawl. He accepted the proposal,
then added: "The only thing I would want to add to the plan is Agent-Reach. I do not want that
particular app installed into this, but rather the ideas and functions of that brought over."

**Ground rules for every unit:**
- worldmonitor and firecrawl are AGPL. We take ideas and sources only, no code.
- globalhackmap is MIT, but it's TypeScript on Mapbox, so anything taken is rewritten.
- Every source uses an official API, or pages a site publishes for readers.
- No copied logins, no spoofed user agents, no headless browsers to get past a wall, and nothing
  a platform's terms forbid (Facebook, Instagram, Nextdoor, Xiaohongshu, LinkedIn job search,
  X without its paid API). A refusing site is paused (`RefusedHostPause`).
- Not built:
  - sherlock: people-search.
  - RansomLook leak-site claims.
  - Submarine cable map: licensed non-commercial only.
  - EONET: today it is 29 US wildfires already in NIFC, 11 sea-ice items and 2 storms already in GDACS.
  - firecrawl: `read_web_page` already does this.

**Probed 05-11-26 (one plain request each):**
- Answer 200 with no key:
  - GDACS: the `SEARCH` endpoint answers; `MAP` gives 400. It returns at most 100 rows, and green wildfires fill them, so wildfires get their own query.
  - IODA v2, WMO `wmo_all.json`, FAA `nasstatus`, SWPC ovation, CelesTrak, CIRCL `/api/last`, GitHub advisories.
  - HN Algolia, Apple Podcasts search, GitHub REST, YouTube oEmbed, Mastodon, and Bluesky reads (`getAuthorFeed`).
- Bluesky search answers 403 without a session, so it needs an app password.
- Reddit `.json` answers 403 anonymously, so it is reached only through its official app sign-in.

## Unit 1 - Global hazards and convergence (Overwatch) - DONE 05-11-26 (82d3922, deployed)

Live run 05-11-26:
- GDACS: 2 cyclones, 12 floods and 1 drought current. Floods' "Magnitude 0" is dropped.
- IODA: 17 countries. Our own country names are used, and start times show the date.
- Convergence: no square converged that night, which is expected without stories. The briefing names the top two when some do.
- The server reaches both GDACS and IODA.


- [x] **GDACS** (UN global disaster alerts) into `HazardLayerService`.
  - New kinds: cyclone, flood, volcano, drought, plus wildfire outside the US (NIFC covers the US).
  - GDACS earthquakes are skipped, because USGS already covers the world.
  - Red, Orange and Green map to major, moderate and minor.
  - Current events only, cached 15 min.
- [x] **IODA internet outages:** country-level events in the last 24 h, at the country centroid
  (`WorldNewsPlaces`).
  - Severity is how many independent IODA signals agree: routing (BGP), active probing, Google traffic.
  - A Google-traffic-only drop shows as minor, since it's often noise.
  - Links to IODA's country page.
- [x] **Convergence alerts:** worldmonitor's idea, our own code.
  - Grid: 1° cells over a 24 h window.
  - Point signals: city-placed World News situations, earthquakes, fires, flooding gauges, GDACS events, power outages.
  - Country-wide signals: internet outages and country-placed news count in every cell of that country.
  - Moving traffic (aircraft, ships) is left out, because it is everywhere and isn't an event.
  - A cell converges with 3 or more distinct kinds, at least 2 of them placed in the cell.
  - Shown as an outlined cell with the contributing items, and as a "Where signals converge" section in the Overwatch briefing and `overwatch_briefing`.

## Unit 2 - SentinelGPT reach (Agent-Reach's functions, our code) - DONE 06-11-26

Live run 06-11-26:
- GitHub, Hacker News, YouTube (oEmbed), Bluesky, Mastodon, Podcasts and X (embed) read real links.
- Search worked on GitHub, Hacker News and Podcasts.
- Check all: every channel working, except Reddit ("Not set up").
- Literal private IPs (127.0.0.1, 169.254.169.254, 10.x) are refused on connect.


Agent-Reach gives an agent one way to search and read platforms it can't reach on its own. It lists
"channels", routes a link to the right reader, and has a "doctor" that checks each channel. Here
that becomes a toolset beside `search_web` and `read_web_page`:

- [x] **`read_link`**: picks the reader by the address and falls back to `read_web_page`.
- [x] **GitHub** (REST; `Reach:GitHubToken` raises 60/h to 5,000/h): search repos, read a repo and its README, read an issue or PR with its comments.
- [x] **YouTube:**
  - With `Reach:YouTubeApiKey`: search, video and channel details, through the Data API.
  - Without the key: title and channel from oEmbed.
  - No transcripts: YouTube's API gives captions only to a video's owner.
- [x] **Hacker News** (Algolia): search, and read a story with its comment thread.
- [x] **Reddit** (official app sign-in, `Reach:RedditClientId`/`RedditClientSecret`): search, read a post with comments, list a subreddit. Off until set.
- [x] **Bluesky:**
  - Without a session: read a profile's posts or a thread.
  - With `Reach:BlueskyHandle` + `Reach:BlueskyAppPassword`: search.
- [x] **Mastodon:** read a post or thread by its link, on any server.
- [x] **Podcasts** (Apple Podcasts search, then the show's RSS): episodes, and the transcript when the show publishes one.
- [x] **X:**
  - Without the token, a post's own text from X's embed service (`publish.x.com/oembed`).
  - With `Reach:XBearerToken` (official API v2, paid): search recent posts, replies and numbers.
- [x] **Channels and doctor:**
  - `list_channels` tells the model what it can reach.
  - Settings shows each channel as ready, needs a key (and which one), or paused because the site refused, with a Check button.

## Unit 3 - Small global layers (Overwatch)

- [ ] WMO severe-weather alerts outside the US (WEATHER, beside the NWS alerts).
- [ ] FAA airport ground stops and delays (AIR).
- [ ] NOAA SWPC aurora oval and Kp index (new SPACE group).

## Unit 4 - Cyber incidents on the globe (Overwatch)

- [ ] A "cyber" World News kind, so breach, ransomware and outage headlines are placed through
  the existing pipeline.
- [ ] Honest status labels (globalhackmap): Confirmed / Reported / Claimed / Precautionary /
  Disputed, from the headlines.

## Unit 5 - Satellites (Overwatch)

- [ ] CelesTrak stations and visible groups, refreshed every 2 h as CelesTrak asks, drawn on orbit in Cesium (SPACE).

## Unit 6 - Photo placement (Overwatch)

- [ ] Drop a photo on the search bar: read its EXIF GPS, time and heading in the browser and fly
  there. Nothing is uploaded.

## Unit 7 - Threat Intel additions

- [ ] New-CVE ticker (CIRCL, minutes after publication).
- [ ] Malicious-package advisories (GitHub, `type=malware`).
- [ ] "Am I affected?": pick the products you use, and get alerted when they appear in CISA KEV
  (already downloaded) or in new critical CVEs.
- [ ] SEC 8-K Item 1.05 cyber disclosures. SEC requires requests to state a contact address in the User-Agent header, so this stays off until `SecEdgar:ContactEmail` is set.

## Needs Grant

- Keys, each optional. Set each with `gh secret set NAME`, then run the "Sync droplet .env keys" workflow:
  - `REACH_GITHUB_TOKEN`
  - `REACH_YOUTUBE_API_KEY`
  - `REACH_REDDIT_CLIENT_ID` + `REACH_REDDIT_CLIENT_SECRET`
  - `REACH_BLUESKY_HANDLE` + `REACH_BLUESKY_APP_PASSWORD`
  - `REACH_X_BEARER_TOKEN`
  - `SecEdgar:ContactEmail` (unit 7)
- Later, if wanted: NASA FIRMS, Cloudflare Radar and ACLED keys.
