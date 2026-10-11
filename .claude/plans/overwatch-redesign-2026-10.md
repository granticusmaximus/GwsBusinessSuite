# Overwatch redesign + directions + street view (proposed 2026-10-10)

Grant (2026-10-10): make Overwatch take more of the screen, "restructure the entire page to be
more user friendly", add step-by-step directions for a planned route (like Google/Apple Maps),
print/share those directions, and a drag-a-person street view.

**Status: APPROVED by Grant 2026-10-10 ("Approve as written").** Decisions: keep the green tactical look; side panel on the left; Mapillary token IS set on the droplet.

## What's wrong today

- 12 layer buttons + incident-type chips in one strip across the top: they run off the right edge
  (FAVORITES, the fullscreen button and the status text are cut off) and sit on top of the globe.
- Four floating panels (Route/Trips, Areas & captures, Favorites, Coverage) open independently,
  overlap each other and cover the map.
- The page header + description and the long attribution footer take ~25% of the height.
- Fullscreen hides the sidebar/top bar but the globe keeps its old height.

## New layout (Google Maps-style)

```
+--------------------------------------------------------------------------------+
| [=] [ Search a place or address...      (dir) ]        [ cams: 1,500 ]  [i] [?]|
| +--------------------------+                                         +-------+ |
| | EXPLORE | DIRECTIONS |   |                                         |LAYERS | |
| |  SAVED  | AREAS          |                                         +-------+ |
| |--------------------------|                                                   |
| | (panel content for the   |              G L O B E                            |
| |  chosen tab: results,    |           (fills the whole                        |
| |  route + turn-by-turn,   |            content area)                    [+]   |
| |  favorites/trips,        |                                             [-]   |
| |  watched areas...)       |                                            [home] |
| |                          |                                           [person]|
| +--------------------------+                                           [share] |
|                                    [ radar timeline when on ]          [ full ]|
+--------------------------------------------------------------------------------+
```

- **Full-height map.** The page title row is dropped (the globe's own header keeps the name);
  the map fills the content area at every window size; Fullscreen fills the whole screen.
- **One side panel with tabs** instead of four floating panels, collapsible with [=]:
  - EXPLORE: search results, cameras in view list, coverage ("fly there") list.
  - DIRECTIONS: start/stops/destination, depart time, Go; then the route summary, step-by-step
    directions, and everything along the route (cameras, incidents, alerts, ice, signs, ask the
    cameras); save as a trip.
  - SAVED: favorite cameras (with timelapse) and saved trips.
  - AREAS: watched areas and incident captures.
- **Layers button** (top-right) opens a grouped panel instead of the button strip:
  Weather (radar, alerts, road temps) · Traffic (incidents + per-type filters, message signs) ·
  Hazards · Cameras (healthy only, select for watch wall). Active layers show as small chips.
- **Map controls** bottom-right: zoom, home, street-view person, share view, fullscreen.
- **Attribution** moves behind [i]; keyboard shortcuts behind [?] (all current shortcuts kept).
- Narrow windows/phones: the side panel becomes a bottom sheet; layers/controls stay reachable.
- Camera feed, weather, incident and radar pop-ups keep working, positioned so they don't clash
  with the side panel.

## Turn-by-turn directions

- Ask OSRM (already used for routes) for `steps=true`; turn each maneuver (depart, turn, merge,
  ramp, roundabout, fork, arrive) and road name into an instruction: "Turn left onto Peachtree
  St", "Take exit 251 toward I-85 N", with distance per step and per-leg totals.
- Steps listed under the route; clicking one flies the globe there and highlights that piece.
- Times shown in dd-MM-yy HH:mm / 24-hour, per the app-wide rule.

## Print and share directions

- **Print:** a clean print page (`/admin/osint/directions?...`) with the route summary, numbered
  steps, legs, and along-route warnings (incidents, alerts, ice), laid out for paper.
- **Share:** copy the directions as text, copy a link that reopens the same route in Overwatch,
  and the device share sheet where the browser supports it (e.g. Safari/Mac).

## Street view ("drop the person")

- Drag the person icon onto the globe (or click it, then click a spot).
- Google doesn't allow its Street View imagery inside another app without a paid Maps API key,
  so a panel shows:
  - the nearest free street-level photos from **Mapillary** (if its token is set) and
    **KartaView**, with direction and date, browsable left/right;
  - **"Open in Google Street View"**: opens Google Maps' Street View at exactly that spot in a
    new tab (a standard Google Maps link, no key).
- If there's no free imagery nearby, the panel says so and still offers the Google link.

## Order of work (each step verified with the release check, guide updated as it goes)

1. Layout restructure (full-height map, side panel with tabs, layers panel, map controls,
   attribution/help behind buttons, responsive bottom sheet). Existing behaviour unchanged.
2. Turn-by-turn directions in the DIRECTIONS tab.
3. Print page + share/copy.
4. Street view person.

Browser tests updated for the moved controls; new ones for each feature. OVERWATCH_USER_GUIDE
updated throughout.

## Progress

| Step | Status |
| --- | --- |
| 1 Layout restructure | done 2026-10-10: top bar, tabbed left side panel (EXPLORE/DIRECTIONS/SAVED/AREAS), LAYERS panel (always rendered so R/A/I/S shortcuts work), sources/shortcuts popovers, map controls (zoom/home/person/share/fullscreen), full-height shell + fullscreen fills window, bottom sheet under @container gws-main 720px; weather panel and radar bar shift beside the open panel; route no longer cleared on tab change (CLEAR ROUTE). Layout checked with rendered screenshots at 1440/1024/640 (static mirror of the markup + real CSS) |
| 2 Turn-by-turn directions | done: OSRM steps=true -> RouteDirections (tested against a real Macon-Atlanta response) |
| 3 Print + share | done: /admin/osint/directions print page (PrintLayout), COPY/SHARE (navigator.share or clipboard), ?route= links reopen the route |
| 4 Street view person | done, then REBUILT 2026-10-11 per Grant ("build in your own street view, not Google Maps"): Overwatch's own 360° viewer (wwwroot/js/street-view.js, a WebGL equirectangular renderer - no viewer library, no Google). Mapillary is_pano images via StreetLevelImageryService.GetSceneAsync (nearest panorama + one walk link per 45° sector, 2-40 m), pictures proxied by /admin/api/overwatch/streetview/{id} (token stays server-side, same-origin texture). Drag/keys look, wheel/pinch zoom, arrows walk keeping heading, Esc closes. Flat Mapillary/KartaView photos only as the no-panorama fallback. Google link removed. Browser tests read rendered pixels from a generated test panorama |

Not verified live: Mapillary (token only on the droplet) and KartaView (its API timed out from
this Mac on 2026-10-10); parsing is tested against documented response shapes. The real page
(AdminOnly) wasn't opened in a browser here - Grant to look after deploy.
