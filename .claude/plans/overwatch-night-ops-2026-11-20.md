# Overwatch: the Night Ops look, with realistic close-up (approved 20-11-26)

Grant approved the Night Ops kit plus its "close up" proposal on 20-11-26 ("I approve"). The live
mock-up is the reference: https://claude.ai/artifact/1eiEvS3kCnaSdor4iAqBWz (kit tab "Night Ops",
section "Night Ops, close up"). Everything below is to match it on the web/PWA globe (Cesium) and,
as closely as Apple's map allows, the Mac app (MapKit; Catalyst can't link WebGL shaders).

## Decision update (20-11-26): COMBINED design approved
Grant approved "Night Ops × Apple, combined" (kit page section #combined) with one note: everything fully
responsive, web/PWA and the Mac app alike. Combined means:
- Apple structure: sidebar (layers) / map / inspector (selection); toolbar with Look switch
  (Standard, Night Ops, Satellite) and search; floating glass panels ~50% opacity with blur and real
  switches; SF Pro for text, SF Mono for figures (system font stack, Inter/IBM Plex Mono fallback);
  map controls where Apple Maps keeps them; Increase Contrast / Reduce Transparency / larger text honoured.
- Night Ops surface: green on black; kit marker shapes on the map; SF Symbols-style icons in the layer
  list with live counts; hex grid, scan lines, grain, corner ticks; cut corners + status line on panels;
  mono caps labels, Chakra Petch for titles and the stage chip; HUD readout + stage chip; command line (⌘K).
- Severity: Apple system yellow #ffd60a / red #ff453a plus shape and label.
- RESPONSIVE (Grant's note): every new piece works from phone width to wide desktop and in the Mac app's
  resizable window. Sidebar/inspector collapse to sheets/drawers on narrow widths; HUD lines truncate,
  never overflow; touch targets >= 44 px; admin breakpoints via @container gws-main (AGENTS.md).

## The look (from the kit)
- Green on olive-black: accent #59f07a, ink #d4ffd9, muted #5f9a68, ok #59f07a, warn #e8ff4d,
  crit #ff5a3c, info #66e0c8. Tokens as `--ow-*` beside `--neu-*` (Overwatch keeps its own theme).
- Chakra Petch (labels, caps) and IBM Plex Mono (every figure). Dates dd-MM-yy, 24h times.
- Panels: near-black glass, 1 px green edge, 10 px cut corners, status line headers.
- Hex grid underlay at orbit/regional zoom; corner brackets on selection; range rings expand once;
  no idle animation; prefers-reduced-motion stops grain and sweeps.
- Every one of the 31 layers drawn as in the kit (glyph per layer; one shared glyph set for the web
  globe and the Mac bridge, checked by the existing both-apps layer tests).

## Close up (zoom stages)
| Stage | Altitude | What changes |
|---|---|---|
| Orbit | > 2,000 km | Night Ops globe: glowing coastlines, graticule, rim light |
| Regional | 2,000-50 km | Imagery fades in as night-vision green; grain + vignette; hex grid fades |
| Approach | 50-5 km | Relief shading, contours (20 m, bold 100 m), draped rings/routes |
| Ground | < 5 km, tilted | 3D terrain + OSM buildings (wireframe to solid), real heights, thermal tint |

Features, each a switch: night vision, grain/vignette, contours, slope shading, line of sight from
a camera, rings and routes draped on terrain, 3D buildings, night mode (real sun), thermal tint on
hot data, HUD (compass tape, tilt ladder, ALT/GND/GRID/SUN, target brackets with distance and AGL,
aircraft drop lines). Quality setting Off / Standard / High; High never default on battery.

## Mac app
Apple realistic 3D terrain + flyover imagery; green blend layer for night vision; contours, rings,
routes as terrain-following MapKit overlays; HUD in the web view. Line of sight and thermal tint are
web only unless the separate AppKit map window is built.

## Phases
- [ ] 1 Theme tokens, fonts, panels and HUD frame (both apps); layer glyph set (31) on web + Mac bridge
- [x] 2 Zoom stages + night-vision post-process on Cesium (imagery, grain, vignette, hex fade)
- [x] 3 Terrain: Cesium World Terrain (existing key), contours/slope material, clampToGround overlays
- [~] 4 HUD readouts (compass, tilt, ALT/GND/GRID/SUN, brackets, drop lines)
- [~] 5 Buildings (Cesium OSM Buildings), night mode lighting, line of sight, thermal tint, quality setting
- [ ] 6 Mac: MapKit realistic elevation, green blend layer, terrain overlays, shared HUD
- [ ] Each phase: Overwatch layer tests on both apps, verify-release, user guide update

## Progress
- 20-11-26: design approved.
- 20-11-26: Phase 1, part one: --ow-* tokens in tactical-globe.css; every rgba(125,255,176)/#7dffb0 in
  tactical-globe.css and OverwatchGrid.razor.css now reads the tokens (with fallbacks); Chakra Petch +
  IBM Plex Mono loaded in App.razor; panel titles in Chakra Petch; marker green #59f07a on both apps
  (tactical-globe.js, NativeShapes, OverwatchMapLook.Green). Left alone on purpose: map-note colour
  choices (user data), the Field Kit print page, the app-wide boot screen.
  Still to do in Phase 1: cut panel corners, status-line headers, the 31-layer glyph set per the kit.
- 20-11-26: Phase 2 done, gate PASS. Found: Overwatch already had the holo globe, the green-washed street
  fade by height, scan lines, vignette and a 3D terrain/buildings switch. Cesium post-process stages are
  OFF for everyone (Safari/WKWebView Metal rejects them; OverwatchCesiumVersionTests), so night vision is
  NOT a shader: grain is a static SVG-noise layer (.tg-grain), strength by stage via data-ow-stage on
  .tg-shell; stage readout .tg-stage. Stage heights in OverwatchMapLook (OrbitAbove 2,000 km,
  RegionalAbove 50 km, ApproachAbove 5 km) mirrored as STAGES in tactical-globe.js (NativeShapesTests).
  Mac shows no stage until phase 6 reports its camera height. Any new globe shader (globe.material,
  post-process) must be web-non-WebKit only or it breaks Safari and the Mac app.
- 20-11-26: Phase 3 done, gate PASS (incl. Mac build). tacticalGlobe.setTerrainShading(id, 'off'|'contours'|'slope'):
  Cesium ElevationContour (20 m) / SlopeRamp (canvas ramp green>yellow>red) as globe.material, only while
  3D is on (entry.threeDOn); ion terrain now requestVertexNormals. onRenderError removes globe.material
  BEFORE handleShaderFailure, so a browser that can't compile it loses only the shading
  (OverwatchCesiumVersionTests.TerrainShading_ShouldFailSoftBeforeTheGlobeFallback). Page: CONTOURS/SLOPE
  toggles under the 3D toggle (OverwatchGrid.razor SetTerrainShadingAsync). Contract: setTerrainShading in
  NativeOverwatchOperations, NativeShapes.Handled, tactical-globe.js nativeOperations, Mac bridge no-op case.
  Routes/route conditions/drive lines were already clampToGround; measure + note areas stay unclamped on purpose.
  Lesson: any new tacticalGlobe.setX/clearX called from OverwatchGrid.razor must be added to all four places
  (contract All, NativeShapes.Handled or a real handler, nativeOperations list, Mac bridge switch).
- 20-11-26: Phase 4 readout done, gate PASS (one run hung on OverwatchGridScriptBrowserTests.MapControls_ZoomAndHome
  under load; it passed alone in 2 s and the re-run passed). #tg-readout line: HDG, TILT (>5°), ALT, GND at view
  centre (globe.getHeight), lat/lon, SUN az/el. sunPosition() in tactical-globe.js = OverwatchMapLook.SunPosition
  (NativeShapesTests checks the formula and the solstice value). updateReadout runs from the holo fade handler,
  throttled 150 ms. NOT done in phase 4: target corner brackets on a selected entity, aircraft drop lines.
- 20-11-26: Phase 5 part one done, gate PASS: night mode from the sun at view centre (data-ow-night day/dusk/night
  on .tg-shell; CSS brightness/contrast filter on .tg-viewport and stronger grain; no lighting shader). OSM
  buildings styled color('#7fe39a', 0.78).
- STILL TO DO: phase 1 rest (31-layer glyph set), phase 4 rest
  (brackets), phase 5 rest (line of sight from a camera, thermal tint on fires/engines, quality
  setting Off/Standard/High), phase 6 (Mac: report camera height so stage/readout/night show; MapKit
  realistic elevation; green blend; overlays), and the Apple-edition decision (kit page, not yet answered).
- 20-11-26: Phase 1 cut corners done, gate PASS: .tg-stream-panel and .tg-watch-wall-panel clip top-right and bottom-left at 10 px (top-left drag, bottom-right resize stay square), accent diagonals via ::before/::after.
- 20-11-26: Phase 1 status lines done, gate PASS: addStatusLine() in openAsModal puts "KIND ▸ HH:mm · LIVE" under every modal panel title (.tg-status-line; data-ow-kind / data-ow-state override). Phase 1 left: the 31-layer marker set.
- 20-11-26: Phase 4 drop lines done, gate PASS: in 3D the selected aircraft gets a dashed line from the ground to its altitude (aircraftAltitudeMetres, path.altitudeMetres) with a labelled point. Phase 4 left: target brackets.
- 20-11-26: Grant asked to see Night Ops and the Apple edition combined; kit page has "Night Ops × Apple, combined" (option C). The 31-layer marker set waits for his pick.
- 20-11-26: COMBINED design approved (see Decision update). Next units, in order:
  A. Responsive pass on everything built so far (readout, stage chip, status lines, panels, shading toggles).
  B. 31-layer marker set in kit shapes (web) + SF Symbols already on Mac; sidebar icons SF-style.
  C. Apple structure: sidebar/inspector/toolbar layout, floating glass panels with cut corners, Look switch.
  D. Phase 4 target brackets; phase 5 line of sight, thermal tint, quality setting.
  E. Phase 6 Mac (camera height to the page, MapKit realistic elevation, green blend, overlays).
