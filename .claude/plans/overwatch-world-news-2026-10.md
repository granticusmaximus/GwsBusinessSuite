# Overwatch × World News (approved 29-10-26)

Product split Grant agreed with Codex: **World News** stays the place for deep reading, filtering,
source inspection and corrections; **Overwatch** is where news becomes spatial and operational
context. Codex shipped the base (9e72a8d: WORLD NEWS globe layer, Brief me facts, Open in World
News). Claude added Show on Overwatch (`/admin/osint?news={id}`), case-sensitive country matching
and placement-before-store-reads (29-10-26). Grant then approved all 14 enhancements below.

Placement rule throughout: a situation is placed only from what its own headline/summary names
(never the publisher's country), or from a correction made on World News. Every marker says how
precise it is (city-level / country-level / corrected).

## Phases (build in order; verify each before the next)

### P1 Placement data
- [x] **A1 Every country + native-language names.** Natural Earth 50m properties (public domain):
  242 countries, label points, names in 26 languages. CJK/Thai names match without word
  boundaries. Ambiguous English names (Georgia, Jordan, Chad) don't place on their own.
- [x] **A4 City-level placement.** GeoNames cities15000 (CC BY 4.0, credited), trimmed to 50k+.
  A city refines a named country (must be in it); a 1M+ city can place on its own. Common-word
  and first-name city names are skipped. Largest population wins.

### P2 Corrections
- [x] **A3 "Wrong place?" on World News.** Pin to a country (and optional city) or keep off the
  globe; stored in worldnews.db `situation_placements`; layer, Brief me and every feature below
  respect it.

### P3 What the layer shows
- [x] **A2 Operational kinds.** disaster, transport, conflict/unrest, health, politics, business,
  sport. Classified from the situation's stored meaning vectors against prototype phrases
  (embedded once, cached); keyword fallback when the embedder is down. Default chips: first four.
- [x] **A7 Marker encoding + window.** Size by outlets, Active (48h) vs Quiet styling,
  24h / 3 days / 7 days picker.
- [x] **B5 Surging stories.** Outlet count at least doubled in 2h with 4+ new outlets → pulsing
  marker + "SURGING"; opt-in bell alert on World News.

### P4 Operational context
- [x] **B1 Backed by official signals.** Kind-matched USGS quakes / wildfires / river gauges
  (HazardLayerService), NWS alerts, power outages near the placement and within the story's time
  → "Backed by …" on panel and Brief me.
- [x] **A5 Nearby in the panel.** City-level only: closest cameras (open in one click), weather
  alerts, hazards, outages.
- [x] **B6 Capture.** Save situation + sources + summary + nearest camera stills into Incident
  Captures.

### P5 Alerts on your places
- [x] **A6 Area alerts.** City-level (or corrected) placements inside a saved Area raise the
  Area's bell alert; first check per area learns silently.
- [x] **B2 Trip/route warnings.** Operational stories near a watched route (city-level within
  25 mi of the path) or in a country it passes through (stop countries by point-in-polygon).

### P6 Visual
- [x] **B3 Coverage lines.** From reporting countries to the placement, on demand from the panel.
- [x] **B4 Country shading.** Natural Earth 110m polygons; single-hue sequential by outlets on
  placed situations in the window; click → that country's stories.
- [x] **B7 Story playback.** Slider over the situation's events in order; marker moves with
  per-event placement.

## Status (29-10-26)
All 14 built and tested, guides updated (docs/OVERWATCH_USER_GUIDE.md, docs/INTELLIGENCE_USER_GUIDE.md).
Deviations from the plan above: area news alerts need city-level or hand placement (a country's
centre says nothing about an area); route warnings take country-level stories only for transport
and conflict kinds; the outlines live in Infrastructure/WorldNewsPlaces/countries.geojson (served
at /admin/api/world-news/countries.geojson) rather than wwwroot; surge alerts only for the four
operational kinds. Migration AddOverwatchAreaWatchNews defaults existing areas to watch news
(quiet first look via the "news:baseline" seen key).

## Done when
Each phase: unit + browser tests, guides updated (OVERWATCH + INTELLIGENCE), verify-release green.
End: live click-through on the scratch World News store.
