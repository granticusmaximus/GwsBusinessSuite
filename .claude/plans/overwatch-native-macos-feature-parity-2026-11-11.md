# Native macOS Overwatch feature parity

## Goal

Keep Overwatch fully inside GWS Business Suite on macOS without depending on Mac Catalyst
WKWebView WebGL. Preserve the existing authenticated Blazor controls and server-side data
orchestration, while rendering the viewport with native MapKit.

## Architecture

- The hosted Overwatch page remains the authority for permissions, providers, filters, panels,
  routes, saved areas, notes, and World News correlation.
- On Mac Catalyst, `tactical-globe.js` selects a native adapter instead of constructing Cesium.
- The adapter forwards the existing `set*`, `clear*`, camera, route, and selection calls through a
  narrow WKScriptMessage bridge.
- The app embeds an `MKMapView` over the page's `tg-viewport` rectangle and renders annotations,
  polylines, polygons, heat/coverage regions, routes, and selection state from those messages.
- Native map movement and selection are sent back to the page so its existing Blazor callbacks and
  detail panels continue to work.
- Browser and PWA rendering remain on Cesium 1.144.0.

## Progress

- [x] Inventory the current native contract and every web renderer operation.
- [x] Add the trusted native-map bridge and lifecycle management.
- [x] Add the JavaScript native adapter with complete contract-command forwarding and return semantics.
- [x] Implement native annotations, expandable clusters, overlays, routes, camera controls,
  picking, and selection callbacks.
- [x] Map every existing Overwatch operation and prove contract/adapter parity with tests.
- [x] Remove the obsolete native shader interception and GPU-unavailable fallback from the Mac path.
- [x] Scale the native map viewport from WebView CSS pixels into UIKit coordinates and initialize
  it at the world view; retain the page's zoom controls through the MapKit adapter.
- [x] Build and run authenticated native acceptance on this Mac.
- [x] Run focused browser/PWA regression tests (in the full suite, 12-11-26).
- [x] Run `./scripts/verify-release.sh`.
  (12-11-26: full default gate PASS on the Mac — restore, audit, build, full suite incl. Playwright, Docker Compose, responsive lint, whitespace; PARTIAL only for deployed-site checks.)

### Verification note (11-11-26)

- Reproduced the stuck native boot screen in the installed app, confirmed the Mac flag, message
  handler, and adapter were active, and identified the opaque WKWebView root covering MapKit.
- Rebuilt and installed the Release Catalyst app with transparent root compositing. Authenticated
  native acceptance visibly showed the MapKit hybrid globe, camera pins, numbered clusters,
  labels, and the existing Overwatch controls together inside the app; the boot screen cleared.
- Batched large native annotation and overlay updates to avoid one MapKit layout pass per pin.
- Follow-up interaction fix: pan now uses MapKit's projected screen coordinates; cluster taps zoom
  to member pins, and a drag release no longer fires a second tap on the web overlay.

- Focused native bridge tests passed (2/2).
- `verify-release.sh --server-only --skip-tests` passed restore, dependency audit, Release build,
  and whitespace checks. Docker Compose and responsive lint could not run because Docker and
  Python are unavailable; the full suite was skipped in this fallback run.
- The default gate could not restore/build MAUI targets because the required workloads are absent.
  Its test host remained active for more than six minutes without reporting a result and was
  stopped. Mac Catalyst build and authenticated acceptance remain outstanding.

### Interaction pass (12-11-26)

Grant reported that pin clicks on the Mac "may or may not open the correct modal", and that pan
and zoom lag. Causes found and fixed:

- [x] Hit test measured from the pin's coordinate; MapKit draws the balloon above it, so clicks
  picked neighbours. Now hit-tests the drawn annotation views (balloon frame, then nearest within
  24pt), clusters included.
- [x] `SelectAnnotation` on an already-selected pin fires nothing, so a second click failed. Now
  opens directly (`Activate`); callouts off; MapKit's own selections are deselected at once.
- [x] Non-clickable layers (ships, AQI, coverage, alert highlights, route marks) competed in the
  hit test and opened the hazard window. `NativeOverwatchOperations.Selectable` and the JS
  `nativeSelectionHandlers` table now match the Cesium click handler (test keeps them equal).
- [x] Satellites and news shading opened the hazard window; aircraft opened nothing (needed a
  Cesium viewer). A `nativeEntry` stand-in now backs aircraft (path via `setAircraftPath`,
  fit via `flyToBounds`) and camera select mode / watch wall.
- [x] Any 1px move counted as a drag and ate the click. 5px threshold.
- [x] Lag: every pointermove and wheel tick was its own message; each wheel tick an animated 2x
  `SetRegion`. Now summed per animation frame; pan uses `SetCenterCoordinate`, wheel/pinch
  `zoomBy` scales the camera distance around the pointer. Region reports wait for release.
  Unchanged layers are skipped and pins are diffed by payload instead of all re-added.
- [x] The injected bootstrap's own gesture code (and its document-wide MutationObserver) removed;
  tactical-globe.js owns forwarding. Older app builds still set `gwsNativeAppNavigation`, and the
  page keeps leaving those alone.
- [x] Browser tests drive the Mac adapter in Chromium with a recording handler (`MacApp_*`);
  Mac app Debug and Release builds clean.
- [ ] On-screen check in the installed app after deploy + app update (not possible from here).

## Acceptance

- Overwatch never leaves the Mac app.
- The Mac path never initializes Cesium or displays the GPU/browser-unavailable message.
- Every operation in `NativeOverwatchOperations.All` has an explicit native rendering or native
  interaction implementation; unsupported silent no-ops are not acceptable.
- Pins do not bleed through the back of the map, clustered counts are visible, and selecting an
  actionable pin opens the same detail workflow as the web renderer.
- Browser and PWA behavior remains unchanged.

## 19-11-26: Mac map styled like the web globe

- [x] `MKMapView`: dark interface style (dark labels), `PointOfInterestFilter = ExcludingAllFilter`,
  and a world `MKPolygon` ("gws-dim", black at 30%) at `AboveRoads`, so the imagery is dimmed under
  the labels like the web's darkened tiles.
- [x] Colours: `NativeShapes.ToneHex` is the web palette; the bridge's pins, clusters, areas and
  lines use it (no `UIColor.System*` left). `DarkGlyph` puts the web's near-black glyph on the
  bright tones. Guarded in `NativeShapesTests.MacMapPalette_ShouldMatchTheWebGlobe`.
- [x] 19-11-26: flat pin discs like the web's in place of MapKit's balloons. `Disc(style)` (colour
  fill, near-black rim, SF Symbol in the glyph colour, cached per style) and `CountDisc(count)` for
  groups, on plain `MKAnnotationView`s with circular collision. HitTest already measures drawn
  frames, and a disc is centred on its point, so clicks land where they look. Guards updated.
- [ ] Grant: look at it on screen after `open gws`.
- [x] 19-11-26 19:34: the Mac app crashed again zooming (same EXC_BAD_ACCESS at 0x20 in MKQuadTrie insert, via
  requestAddingClusterForAnnotationViews), on the 18:45 build that no longer builds its own clusters. So
  MapKit's grouping itself is the fault on macOS 27: pins no longer set a ClusteringIdentifier; overlap is
  left to collision and display priority. Guard in `OverwatchPinRenderingTests`. Grouped labels
  (ClusterLabel) still apply on the web globe.
- [x] 19-11-26 evening (Grant: both Overwatch views identical, a military terminal in greens and blacks):
  `OverwatchMapLook` (Application) is the one recipe: Esri World Imagery, brightness 0.55, contrast 1.35,
  saturation 0, gamma 0.9, then #19ff7a at 26%. Web: the base layer takes those values and
  `addTerminalWash` adds the green layer. Mac: `TerminalImageryOverlay` replaces Apple's map
  (CanReplaceMapContent) with the same tiles through the same steps in Core Image; the dim polygon is
  gone. Drive mode's blue and the time-machine slider's purple are green. Guard:
  `NativeShapesTests.BothMaps_ShouldUseTheSameTerminalLook`. Grant: check on screen (`open gws`).
- [x] 19-11-26 night: **Holo Wireframe (Grant's option B), identical on web and Mac** (kit:
  claude.ai artifact "Overwatch Holo Layer Kit"). Replaces the tinted-imagery look Grant rejected.
  - `OverwatchMapLook` holds every number; web `HOLO` + `applyHoloLook` (black-green globe, land and
    glowing coasts from `/data/overwatch-land.json`, 15° grid, Esri Dark Gray streets plus green wash
    fading in by camera height); Mac `HoloMap` (Apple's flyover globe kept for the round view, an
    opaque veil above its labels, the same land, coasts, grid and streets through the same steps in
    Core Image, faded by camera distance). Test: `BothMaps_ShouldDrawTheSameHoloLook`.
  - Palette: categories all green, amber moderate, red major (`NativeShapes.ToneHex`; web colours).
  - Counts: `NativeShapes.Group` (Mac, regrouped on zoom; count disc taps zoom in).
  - Callouts: `OverwatchCallouts` picks three (World News, conflict) on the server; both apps draw them.
  - Focus: `OverwatchFocus` catalog + `focusLayer` (web dims tg-* entities, Mac dims views and overlays).
  - Not done: per-layer "zoom in" hints in the layer list.
  - Grant: check on screen (`open gws` and the web app).
- [x] 20-11-26 Mac black globe: root cause found. In a Mac Catalyst WKWebView (macOS 27.3) WebKit's GPU
  process can't compile any Metal shader ("Compiler failed to build request"), so every WebGL program
  fails to link (context-lost error at link). Contexts and clears work, so Cesium starts and draws black.
  The same page in an AppKit WKWebView draws the holo globe. Proven with scratch rigs (Catalyst UIKit
  app + AppKit app loading the real tactical-globe.js); no WebKit feature flag
  (UseGPUProcessForWebGL off = no WebGL at all, ForceWebGLUsesLowPower) changes it.
  - `webglCanLinkShaders()` in tactical-globe.js replaces the probe/opt-in: the Mac runs the shared
    globe whenever a trivial program links, else the Apple map at once (no black screen, no reload),
    with one /admin/logs line. The globe returns by itself once Apple fixes WebKit.
  - Truly one globe on the Mac today would need the Overwatch page in an AppKit WKWebView (an AppKit
    helper/bundle), not the Catalyst one. Not built; Grant's call.
