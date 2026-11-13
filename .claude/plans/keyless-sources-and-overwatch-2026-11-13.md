# Key-less sources everywhere, and Overwatch additions (13-11-26)

Grant (13-11-26): replace every part of the app that needs an API key with a key-less alternative,
and propose more Overwatch layers/features. Rules unchanged: free, key-less, no social-network
scraping, respect refusals.

Out of scope (credentials for Grant's own accounts, not data sources): SMTP/Gmail, Stripe,
Turnstile, OAuth/automation credentials, social publishing, backups key, TURN, Mac device secret,
the app's own developer API keys. The Georgia legislature "key" is the site's public one (no change).

## Key inventory (endpoints probed 13-11-26)

| Key | Used for | Key-less replacement | Coverage after |
| --- | --- | --- | --- |
| CongressApi:ApiKey (else DEMO_KEY) | Civic Watch bills, hearings, Federal feed | GovInfo BILLSTATUS bulk XML (200), House Clerk roll-call XML (200), docs.house.gov committee calendar | Full for bills + House; Senate votes still refuse the server |
| ThreatIntel:FocsecApiKey | Domain intel | RDAP (rdap.org) + crt.sh certificates (both 200) | Registration + certificate history; no VPN/proxy scoring |
| ThreatIntel:OtxApiKey | OTX pulses | CISA KEV (200), Feodo Tracker (200); URLhaus/ThreatFox downloads now need auth (410) | No pulse equivalent |
| CameraIntel:MapillaryAccessToken | Street-level imagery | Panoramax (open, 200) | Strong in Europe, thinner elsewhere |
| CameraIntel:DatumfeedApiKey | Camera aggregator (optional bearer) | Works without the key already: drop the setting | Same |
| CameraIntel:WsdotAccessCode | WSDOT cameras | Datumfeed already carries WSDOT: drop the provider's key path | Same |
| CameraIntel:WindyApiKey | Webcams worldwide | Official feeds already built (511s, London, Toronto, ...) | Loses Windy's tourist webcams |
| Overwatch:AisStreamApiKey | Ships worldwide | Finland Digitraffic AIS (200, Baltic) + Norway's open AIS feed | Baltic + Norwegian waters only |
| Overwatch:TomTomApiKey | Traffic flow worldwide | HPMS + NYC speeds already built | US/NYC only |
| Overwatch:CesiumIonToken | 3D terrain + OSM buildings | Terrain from AWS open terrain tiles (200); imagery already Esri/GIBS | Terrain yes; no 3D buildings |
| OllamaWeb:ApiKey | SentinelGPT live web search | Self-hosted SearXNG on the AI droplet, or Wikipedia + GDELT + the app's crawler | SearXNG: full; otherwise news/encyclopedia only |
| Reach:GitHubToken | GitHub reach | Already works without (60 requests an hour) | Same |
| Reach:YouTubeApiKey | YouTube search | Channel RSS feeds (200) | Followed channels, no search |
| Reach:BlueskyHandle/AppPassword | Bluesky search | Public AppView author feeds (200); search needs sign-in (403) | Followed accounts, no search |
| Reach:RedditClientId/Secret, XBearerToken | Reddit, X | None within the no-social-scraping rule | Removed |

## Overwatch: ten more (beyond the earlier list)

1. Time slider for every layer: replay the last 72 h of all layers together, with "what changed".
2. Layer health panel: each layer's source, freshness, last error and coverage, shown honestly.
3. Satellite scenes on click: latest Sentinel-2/Landsat via the key-less Earth Search STAC API, with a before/after slider.
4. Night lights (NASA Black Marble via GIBS): nightly lights against a baseline to spot blackouts.
5. Chokepoints and ports: daily transits through Hormuz, Bab-el-Mandeb, Suez, Panama, Bosporus (IMF PortWatch open data).
6. Flight anomalies: emergency squawks (7500/7600/7700), holding patterns, sudden diversions, as alerts.
7. Sun and shadow tool: sun position and shadow length for a place and time, to check when a photo was taken.
8. Analyst tools: distance and range rings, bearing lines, drawn areas, saved privately to Sentinel.
9. Briefing export: the current view, layers, legend, sources and times as a PDF (ties to N14 dossiers).
10. Multi-signal area rules: alert when several layers fire together in a drawn area (jamming + closure + outage + news surge).

## Progress

- 13-11-26: inventory and endpoint probes done. Grant chose: key-less by default, keys optional
  (they add coverage); SearXNG on the AI droplet for web search.
- 13-11-26 built (tests: KeylessWebSearchTests, StreetLevelImageryServiceTests, ShipServiceTests,
  OverwatchTerrainTests):
  - Web search: OllamaWebSearchService uses SearXNG (OllamaWeb:SearxngUrl, compose SEARXNG_URL) when
    there's no Ollama key; pages fetched directly through the "web-fetch" client (ConnectCallback
    refuses private addresses). scripts/setup-searxng.sh installs it on the AI droplet.
  - Street view: Panoramax 360° panoramas and photos by default ("px-" ids), Mapillary with a token.
  - Ships: Finland's Digitraffic open AIS for the Baltic without a key (1-minute snapshot); aisstream adds the world.
  - 3D: key-less terrain (AWS Terrain Tiles via /admin/api/overwatch/terrain proxy, Cesium
    CustomHeightmapTerrainProvider); buildings still need the ion token. Checked live over the Alps.
  - Already key-less or optional, unchanged: RDAP domain intel (Focsec optional), Datumfeed, WSDOT,
    traffic (HPMS/NYC; TomTom optional), GitHub reach, Congress (DEMO_KEY), Windy (optional).
  - Not replaced: YouTube/Bluesky search (no key-less search), Reddit/X (no-social-scraping rule), OTX pulses.
- Waiting on Grant: run scripts/setup-searxng.sh on the AI droplet and set SEARXNG_URL in the web .env
  (Claude's access to both droplets is blocked by the classifier).
- Overwatch ten features: proposed, not started.
