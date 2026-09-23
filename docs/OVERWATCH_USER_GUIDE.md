# Overwatch — User Guide

Overwatch (`/admin/osint`) is a real-time 3D globe for viewing legitimate, publicly-provided
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
4. [Coverage index](#coverage-index)
5. [Hovering to identify a place](#hovering-to-identify-a-place)
6. [Searching for a location](#searching-for-a-location)
7. [Weather on zoom](#weather-on-zoom)
8. [Traffic incidents](#traffic-incidents)
9. [NOAA radar and severe weather alerts](#noaa-radar-and-severe-weather-alerts)
10. [Watch wall: viewing multiple cameras at once](#watch-wall-viewing-multiple-cameras-at-once)
11. [Favoriting a camera](#favoriting-a-camera)
12. [Analyzing a camera snapshot](#analyzing-a-camera-snapshot)
13. [Sharing a view with a teammate](#sharing-a-view-with-a-teammate)
14. [Moving and resizing a feed/incident window](#moving-and-resizing-a-feedincident-window)
15. [Keyboard shortcuts](#keyboard-shortcuts)
16. [Who can see this](#who-can-see-this)
17. [Known limitations](#known-limitations)

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

At a wide zoom, cameras that are close together on screen group into a single numbered marker
instead of covering the view in overlapping dots — this matters most over Georgia and Datumfeed's
denser cities, which each have thousands of cameras. Click a numbered marker (or just zoom in) to
have the camera fly closer and split the group back into individual, clickable pins.

## Coverage index

Click **COVERAGE** (or press **C**) for a list of every region with confirmed, zero-registration
camera coverage. Click **GO** next to any region to fly there directly instead of manually
searching or panning — useful as a starting point if you don't already know where coverage exists.

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

While ALERTS is on, any camera pin that currently falls inside an active alert's polygon is
itself highlighted — recolored and enlarged to match that alert's severity color — so a camera
sitting in the middle of a warning stands out on the globe without having to compare pin
positions against the alert overlay by eye. The highlight clears the moment ALERTS is turned off,
or if a later refresh shows the alert no longer covers that camera. A camera under more than one
overlapping alert shows the more severe one.

## Watch wall: viewing multiple cameras at once

Click **SELECT** (or press **S**) to enter selection mode — clicking a camera pin now adds it to
a running selection (shown with a highlighted outline) instead of opening its feed immediately. A
floating "SELECTED: N — OPEN WATCH WALL" button appears once at least one camera is selected;
click it to open every selected camera's feed at once in a grid, each tile refreshing
independently. The watch wall panel can be dragged and resized just like a single feed window (see
[Moving and resizing a feed/incident window](#moving-and-resizing-a-feedincident-window)), and each
tile has its own close button if you want to drop one camera without closing the whole wall.
Turning **SELECT** back off clears the current selection.

## Favoriting a camera

Open any camera's feed and click the star (☆/★) in its window's title bar to bookmark it — the
star fills in once saved. Click **FAVORITES** (or press **F**) to see every camera you've
favorited, with a **GO** button that flies to it and reopens its feed directly, even if that
camera isn't currently showing on the globe. Favorites are tied to your admin account, not your
browser, so they follow you across devices and after a page reload.

## Analyzing a camera snapshot

Open any still-image camera's feed and click **ANALYZE** in its window's title bar to get a
plain-English description of what's currently in the shot (e.g. "light traffic, clear skies," or
"vehicle stopped in the right lane, otherwise clear"). The button reads "ANALYZING..." while the
description is generated, which can take a little while depending on local model load — this
runs against a locally-hosted vision model, not a live security feed, and is a one-time,
on-demand look rather than continuous monitoring. Live-video (Hls) cameras don't support this
yet. If nothing happens, a vision model may not be configured — see the SentinelGPT tab in
Settings.

## Sharing a view with a teammate

Click **SHARE VIEW** next to the search box to copy a link to your clipboard that encodes exactly
where the globe is currently looking and whichever camera(s) are open — a single feed, or an
entire watch wall. Opening that link takes another admin straight to the same view. The recipient
still needs their own valid admin login; a share link only encodes the view itself, not access to
the page.

## Moving and resizing a feed/incident window

Every camera feed and incident-detail window can be dragged by its title bar to anywhere on
screen, and resized by dragging the small handle in its bottom-right corner — useful for pulling
up several feeds at once, or enlarging one to see more detail. A window stays wherever you leave
it and at whatever size you set until you close it or open a new one in its place.

## Keyboard shortcuts

- **R** — toggle radar
- **A** — toggle severe weather alerts
- **I** — toggle traffic incidents
- **C** — toggle the coverage index
- **S** — toggle selection mode for the watch wall
- **F** — toggle the favorites panel
- **Esc** — close whichever feed, incident window, or watch wall is open

Shortcuts are ignored while typing in the search box.

## Who can see this

Overwatch requires the **AdminOnly** policy, same as the rest of the Intelligence cluster.

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
- **The coverage index is a hand-maintained list**, not a live query — it lists the regions known
  to have zero-registration coverage as of when it was last updated, not a real-time count of what
  a given source currently publishes.
- **A shared view link is a snapshot, not a live subscription.** It encodes a position and
  camera(s) at the moment it was copied; it doesn't stay in sync with anything afterward, and a
  hand-edited or corrupted link falls back to the default view instead of erroring.
- **Camera snapshot analysis is on-demand only, not continuous monitoring.** It analyzes exactly
  one frame at the moment you click ANALYZE — nothing watches a camera automatically, and it
  doesn't work on live-video (Hls) cameras. It also requires a vision-capable model configured
  separately from the main SentinelGPT assistant (see Settings), since the main model is
  text-only.
