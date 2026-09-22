# Overwatch Grid — User Guide

Overwatch Grid (`/admin/osint`) is a real-time 3D globe for viewing legitimate, publicly-provided
camera feeds, weather, and traffic conditions anywhere they're available — drag and zoom to any
place on Earth, and it surfaces whatever public data exists for that spot. Everything it shows
comes from free, publicly-provided sources (state DOTs, NOAA/NWS, OpenStreetMap-derived data,
Esri's free basemap tiles) — it never scans for or surfaces unsecured private cameras.

This guide is text-only (no screenshots) — see the note at the end of
[`docs/USER_GUIDES.md`](USER_GUIDES.md) for why.

## Contents

1. [Core concepts](#core-concepts)
2. [Navigating the globe](#navigating-the-globe)
3. [Finding and viewing cameras](#finding-and-viewing-cameras)
4. [Hovering to identify a place](#hovering-to-identify-a-place)
5. [Searching for a location](#searching-for-a-location)
6. [Weather on zoom](#weather-on-zoom)
7. [Traffic incidents](#traffic-incidents)
8. [NOAA radar and severe weather alerts](#noaa-radar-and-severe-weather-alerts)
9. [Moving and resizing a feed/incident window](#moving-and-resizing-a-feedincident-window)
10. [Keyboard shortcuts](#keyboard-shortcuts)
11. [Who can see this](#who-can-see-this)
12. [Known limitations](#known-limitations)

---

## Core concepts

- **The basemap is real aerial photography, not a drawn map.** The globe's imagery (Esri's free
  World Imagery service) is genuine satellite/aerial photography, with a separate transparent
  layer of place names, boundaries, and road/street names on top — the closer you zoom, the more
  detail both layers reveal, similar to a "satellite + labels" view in a consumer map app.
- **Coverage is real but patchy — by design.** Every camera, incident, and weather source here is
  a specific, verified, legitimate public feed. That means coverage is inherently uneven: dense in
  the states/cities a source actually publishes for, and empty everywhere else. The page is honest
  about this rather than papering over gaps.
- **Everything server-side, nothing client-side.** Every camera list, weather lookup, incident
  feed, and search query is fetched by the server, not the browser — several of the underlying
  providers require a real identifying request header or aren't reachable directly from a browser
  at all, so all of this happens behind the scenes regardless of what's driving it.

## Navigating the globe

Left-click-drag rotates the globe; scroll (or pinch, on a trackpad) zooms in and out. Zooming is
tuned to reach a genuinely close, street-level view within a realistic amount of scrolling rather
than requiring dozens of scroll notches.

## Finding and viewing cameras

Every green dot on the globe is a real public traffic/webcam feed. Click one to open a feed
window (see [Moving and resizing a feed/incident window](#moving-and-resizing-a-feedincident-window))
showing either a live video stream or a periodically-refreshed still image, depending on what that
camera's source provides. If a feed goes down, the window shows "FEED UNAVAILABLE" instead of a
broken image.

If a region shows no cameras at all after the view settles, a hint explains that this is a
genuine coverage gap, not a loading failure — try Georgia, Washington State, or Datumfeed's
covered cities (Austin, California, Ontario, Ottawa, Toronto, London) for guaranteed coverage.

## Hovering to identify a place

Once zoomed in close enough to make it meaningful, hovering the cursor over the globe (and
pausing briefly) shows a small tooltip naming whatever's there — a business, landmark, or street
address. This only appears for places that are actually named in the underlying public map data;
hovering over an ordinary, untagged building will show nothing rather than a guess. See
[Known limitations](#known-limitations) for why that gap exists and isn't fully fixable with free
data sources.

## Searching for a location

Type a city, address, or place name into the search box in the top-left and press Enter or click
**GO** to fly the camera there. Real, current addresses (including exact residential addresses)
are supported — the search falls back to a second, address-specialized data source if the first
one doesn't recognize what you typed.

## Weather on zoom

A weather panel in the bottom-left always reflects current conditions and a short forecast for
wherever the globe is currently centered, updating automatically every time you pan or zoom to a
new area — no toggle needed. It shows the live temperature and conditions from the nearest real
weather station when one is available, falling back to the forecast's own temperature if a
station's current reading can't be reached at that moment.

## Traffic incidents

Click **INCIDENTS** (or press **I**) to show real-time accidents, roadwork, and road closures as
colored dots — red for accidents/incidents, orange for closures, yellow for roadwork. Click one to
see its full description, affected roadway, and lane/closure details in its own window. This
currently covers Georgia; see [Known limitations](#known-limitations) for other states.

## NOAA radar and severe weather alerts

**RADAR** (or **R**) overlays live NOAA weather radar. **ALERTS** (or **A**) draws any currently
active NWS severe weather warning as a colored polygon over the area it covers, colored by
severity (extreme/severe/moderate).

## Moving and resizing a feed/incident window

Every camera feed and incident-detail window can be dragged by its title bar to anywhere on
screen, and resized by dragging the small handle in its bottom-right corner — useful for pulling
up several feeds at once, or enlarging one to see more detail. A window stays wherever you leave
it and at whatever size you set until you close it or open a new one in its place.

## Keyboard shortcuts

- **R** — toggle radar
- **A** — toggle severe weather alerts
- **I** — toggle traffic incidents
- **Esc** — close whichever feed or incident window is open

Shortcuts are ignored while typing in the search box.

## Who can see this

Overwatch Grid requires the **AdminOnly** policy, same as the rest of the Intelligence cluster.

## Known limitations

- **Hover-to-identify only finds places that are actually named in the underlying public map
  data.** Most ordinary residential buildings have no business/address tag in that data at all, so
  hovering over them shows nothing — this is a real gap in free, crowd-sourced map data, not a
  bug, and isn't something more code tuning alone can fix without switching to a paid, richer
  places database (not currently in place).
- **Camera coverage is real but regional.** Confirmed, zero-registration coverage today: Georgia
  (statewide), Washington State, and Datumfeed's aggregated cities (Austin, California, Ontario,
  Ottawa, Toronto, London). Optional free API keys (see `CameraIntelOptions.cs`) can add Windy's
  global public webcams or raise Datumfeed's anonymous rate limit, but nothing on the page requires
  them.
- **Traffic incidents currently cover Georgia only** (GDOT's real-time events feed). Other states
  publish similar open data, but each one needs its own verified integration — not yet built.
- **No historical storm-damage layer.** The alerts layer shows currently active severe weather in
  real time; a separate "what actually got damaged after the fact" dataset exists (NOAA's Storm
  Events database) but is a slow-changing historical/bulk dataset, a poor fit for this page's
  live-map interaction model, and hasn't been built as a result.
- **The basemap and its label layers are Esri's free public tile service**, not a paid
  subscription — reliable for normal use, but not covered by a formal usage guarantee the way a
  paid Esri or Google Maps plan would be.
