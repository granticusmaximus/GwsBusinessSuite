# Overwatch: the Night Ops look, with realistic close-up (approved 20-11-26)

Grant approved the Night Ops kit plus its "close up" proposal on 20-11-26 ("I approve"). The live
mock-up is the reference: https://claude.ai/artifact/1eiEvS3kCnaSdor4iAqBWz (kit tab "Night Ops",
section "Night Ops, close up"). Everything below is to match it on the web/PWA globe (Cesium) and,
as closely as Apple's map allows, the Mac app (MapKit; Catalyst can't link WebGL shaders).

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
- [ ] 2 Zoom stages + night-vision post-process on Cesium (imagery, grain, vignette, hex fade)
- [ ] 3 Terrain: Cesium World Terrain (existing key), contours/slope material, clampToGround overlays
- [ ] 4 HUD readouts (compass, tilt, ALT/GND/GRID/SUN, brackets, drop lines)
- [ ] 5 Buildings (Cesium OSM Buildings), night mode lighting, line of sight, thermal tint, quality setting
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
