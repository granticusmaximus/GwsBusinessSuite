# Overwatch: traffic in zoomed-in areas (25-10-26)

Grant asked (25-10-26) for "as much information as possible for vehicle traffic" when the globe
is zoomed in to a city. (A Flock/ALPR camera layer was looked into the same day and dropped at
Grant's call; nothing here touches it.)

## What Overwatch already had

Incidents (20-odd state feeds + 14 WZDx work-zone feeds), message signs (Caltrans, Montana),
~35 camera providers with "ask the cameras" and camera-first incident detection, road temps,
sun glare, routing. Missing: how much traffic each road carries, live speeds, and one place that
sums up the traffic picture for the view.

## Sources checked live (25-10-26)

| Source | Result |
|---|---|
| FHWA HPMS via geo.dot.gov (`Hosted/HPMS_FULL_{ST}_{YEAR}` FeatureServer) | Key-less, every state + DC + PR, 2025 release for all 52. Per road section: AADT, single-unit + combination truck AADT, K and D factors, future AADT, through lanes, speed limit, signals, stop signs, toll, functional class, route signing/number. Downtown Atlanta 3 km box: 564 sections, 160 KB, ~5 s; GA 2025 count query 0.6 s. maxRecordCount 2000, pagination supported. Older years' schemas differ (2023 has `datayear`, 2025 doesn't), so fields are read from each layer first. |
| NYC DOT Traffic Speeds NBE (Socrata `i4gi-tjb9`) | Key-less. ~125 links reporting today, newest ~2 h old: per link speed (mph), travel time (s), polyline, name, borough. |
| Finland Digitraffic TMS | Key-less with gzip + Digitraffic-User header; not built yet (needs station metadata join). |
| Chicago congestion segments (`n4j6-wkkf`) | Stale: last update 2026-04-30. Skipped. |
| NHTSA FARS CrashAPI | 403 (Akamai) from here. Skipped; bulk CSVs are a later option. |
| Overpass (OSM signals, stop signs, crossings, parking) | 38 s for a 3 km box, mirror timed out, 502s. Too slow for an interactive layer; skipped. |
| TomTom flow | Worldwide live congestion but needs a free key - Grant's decision, not built. |

## Build (v1)

1. `HpmsRoadTrafficService` (Application/RoadTraffic): state catalog from the geo.dot.gov
   service list (latest year per state, layer fields + extent read once a day), zoom-gated
   queries (over ~60 km across: nothing; up to 60 km: freeways + principal arterials; up to
   25 km: + minor arterials and major collectors; up to 8 km: every class with a count), cached
   per tile, deduped by object id.
2. `LiveRoadSpeedService` + `NycDotSpeedProvider`: newest reading per link from the last 3 h,
   cached 3 min, only queried when the view overlaps NYC. More cities plug in as providers.
3. `RoadTrafficSummary`: vehicle-miles per day in view, truck share, busiest roads, live speed
   stats, incidents by type.
4. Overwatch: TRAFFIC > CITY TRAFFIC toggle; roads drawn as one GroundPolylinePrimitive
   coloured by vehicles/day (cool ramp - volume isn't congestion), live speeds on top in
   green/amber/red; click a road or speed link for its details; TRAFFIC IN VIEW in EXPLORE.
5. Tests, user guide, verify-release.

## Progress

- **v1 done 25-10-26.** Application/RoadTraffic: `HpmsRoadTrafficService`, `LiveRoadSpeeds`
  (`ILiveRoadSpeedProvider`, `NycDotSpeedProvider`, `LiveRoadSpeedService`), `RoadTrafficSummary` +
  `LiveSpeedSummary`; DI next to the other live layers; OverwatchGrid CITY TRAFFIC toggle, TRAFFIC
  IN VIEW in EXPLORE, `tacticalGlobe.setRoadTraffic` (GroundPolylinePrimitive, PolylineCollection
  fallback) with road and speed pop-ups; 18 tests (RoadTrafficTests); OVERWATCH_USER_GUIDE section.
- Found while building:
  - The 2025 releases carry `route_name` (NY: "GRAND CNTRL PKWY", "Whitestone Expressway"; GA names
    ~29% of counted sections), so unsigned roads use it, and signed ones show it beside the number.
  - NYC DOT status `-101` rows are bad readings (1,850 of 2,000 sampled were 0 mph, the rest up to
    111 mph); only status `0` is used.
  - A 45 km Atlanta view has 6,860 sections: the service keeps up to 12,000 for the summary and the
    globe draws the busiest 4,000 (with a note). Before that split the vehicle-miles undercounted.
  - Ramps filled the busiest list ("Interstate ramp" x3) - left out; identical unnamed sections
    (both directions) count once.
- Checked live on a throwaway instance (real HPMS + NYC DOT): downtown Atlanta 942 sections in 9 s,
  2.7 M vehicle-miles/day, I-75 369,000/day; 45 km Atlanta 39.8 M across GA + NC; Queens with 7 NYC
  DOT links (Van Wyck 4 mph); road pop-up by clicking the map; phone bottom sheet; no console errors.
- **TomTom live flow done 26-10-26** (Grant made a key). `TomTomTrafficService`: flow tiles
  (relative0, cached 2 min, 40,000/day cap) proxied through `/admin/api/overwatch/traffic-flow/
  {z}/{x}/{y}` (AdminOnly; transparent PNG when unavailable), and flowSegmentData lookups
  (cached 2 min per ~10 m point, "too far from a road" 400s remembered, 2,000/day cap). Its
  HttpClient has `RemoveAllLoggers()` because the key rides in the query string.
  - The key is referer-locked to exactly `https://grantwatson.dev/` (admin.gwsapp.net, www. and
    no-referer all 403 InvalidReferer), so the server sends `Overwatch:TomTomReferer`.
  - Overwatch: LIVE FLOW toggle (imagery layer rebuilt every 2 min; HPMS lines dim to 35 %), live
    speed in the road window (clicked point), LIVE SPEEDS button on the busiest roads.
  - Busiest-road points were a section's end vertex, which matched cross streets (I-20 read
    14 mph); now the halfway point along the section (I-20 then read 63).
  - Deploy: `OVERWATCH_TOMTOM_API_KEY` GitHub secret -> ssh-action `envs` -> `sync_env_secret`
    rewrites that one line of the droplet .env each deploy (no-op when the secret is unset).
    Grant sets the secret; Claude can't write to GitHub.
  - Checked live: 79/79 tiles with flow over Atlanta, busiest live speeds, road window live speed,
    key absent from all logs and output.
- **Next candidates:** TomTom incident details (worldwide incidents; watch the daily allowance and
  duplicates of state feeds), more live-speed cities (Finland Digitraffic TMS works key-less; Maryland
  CHART and Houston TranStar to check), FARS crash history via bulk CSVs, camera-based anonymous
  vehicle counts with the local vision model, and TomTom flow if Grant adds a key.
