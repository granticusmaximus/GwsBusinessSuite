# Overwatch: web/PWA matches the Mac look, every layer visually verified on both (26-11-26)

Grant's decisions (26-11-26):
- The Mac app's Overwatch look (Apple map, holo overlays, SF Symbol discs) is the REFERENCE.
- The web/PWA copies from the Mac: markers (coloured discs with an icon, Mac sizes 24/30 px, Mac tone
  colours from NativeShapes.ToneHex, green count discs for crowds), Satellite imagery (closest free
  imagery + labels, like Apple's hybrid flyover), night dimming (plain black 18% dusk / 38% night,
  OverwatchMapLook.DuskDim/NightDim), callouts (Menlo Bold box beside the disc; extra web-only pin
  labels dropped).
- Web keeps its extras (selection brackets, heading rotation, drop lines, thermal tint).
- Don't change the underlying mechanisms: styling only.
- Verify EVERY layer toggle on both apps visually. Mac: a debug-only self-test in a separate Debug
  build, signed in to PRODUCTION (Grant chose production). The installed app is left alone.

## Design
- Web markers: `macMarker(operation, item)` in tactical-globe.js is a port of NativeShapes.Marker
  (same operation names, same rules). SF Symbol names map to Bootstrap Icons 1.11.3 (already loaded
  from the CDN in App.razor) in one table, SF_TO_BI. Disc drawn like MapDelegate.Disc: tone fill,
  1.5 px #05080a rim, icon dark (white on red). Tests: every symbol NativeShapes can return has a
  SF_TO_BI entry; the JS tone table equals ToneHex.
- Mac check: a DEBUG-only Overwatch self-test (env var GWS_OVERWATCH_SELFTEST=1) that opens
  /admin/osint, clicks each [data-tg-toggle] in turn, waits for drawing, renders the window
  (Apple map + page) to PNG, activates the first marker to open its panel and saves that too.
  Output: ~/Downloads/gws-overwatch-selftest/<run>/ (the app is sandboxed; Downloads is allowed).
- Web check: Playwright (Chromium + WebKit) against a local app on a DB copy, same toggle list,
  1440 / 820 / 390.

## Tools
- Tour script (shared): tests/GwsBusinessSuite.Tests/TestData/overwatch-visual-tour.js (window.__owTour(host)).
- Mac: Debug build, launch with GWS_OVERWATCH_SELFTEST=1 -> ~/Downloads/gws-overwatch-selftest/<time>/
  (OverwatchSelfTest.cs + bridge selfTest* ops, all #if DEBUG; tour embedded in Debug only).
- Web: tactical-globe.js visiblePins (read-only) + a scratch Playwright runner against a local app on a
  DB copy (not committed).

## Progress
- [x] 1 Web markers = Mac discs (all layers), count discs for clusters (macMarker/macDisc/macPin in
      tactical-globe.js; old kit glyphs removed except thermal glows and tool dots/wedges)
- [x] 2 Web night dimming = Mac black shade (.tg-viewport::after, hidden under html.gws-native-overwatch)
- [x] 3 Web callouts = Mac style (macCallout); extra pin labels dropped (aircraft, ships, AQI, outages,
      satellites, coverage, convergence, photo, street view, time machine). Kept: map note titles (user
      data) and route stop letters.
- [~] 4 Web Satellite look: already imagery + Esri places/roads labels (= Apple hybrid); colour grade to
      tune against the Mac screenshots
- [ ] 5 Tests, verify-release, user guide
- [ ] 6 Web visual check of every toggle (Chromium + WebKit, 3 widths)
- [ ] 7 Mac self-test (Debug only) and visual check of every toggle against production
- [ ] 8 Findings fixed, report to Grant

## Handoff (27-11-26, before VS Code restart for Screen Recording)
- Done and tested: units 1-3 (web Mac-look markers, night shade, callouts), tests updated; targeted
  Overwatch tests pass (NightOps parity test compares every layer sample against NativeShapes.Marker).
  User guide updated. verify-release NOT yet run.
- Mac Debug build has the self-test (bin/Debug/.../GWS Business Suite.app). In-app capture can't see
  MapKit (Metal): map area comes out grey. Grant is granting Screen Recording to VS Code so the
  capture can be done with `screencapture -l <windowid>` from outside (window id via a small Swift
  CGWindowList tool, scratchpad/wid). Next: make selfTestShot write a request file and wait for the
  external capture, or drive shots from a watcher loop.
- Web tour (Chromium 1440, local app :5299 on a DB copy, scratch runner): every switch turned on
  except live-flow/contours/slope (nested, not found) and coverage/favorites/areas/route (not plain
  toggles); host.select mostly failed to open windows -> fix the runner's pin click (sidebar/panel
  covering pins, or visiblePins coordinates vs device pixels) before judging.
- Still to do: fix the above, full Mac tour with real captures, review all pictures both apps,
  WebKit + 820/390 widths, satellite colour grade, verify-release, report.

## Phase 2 (27-11-26, Grant): the whole app responsive, every page, every version
"Verify that the entire application is completely responsive in every single way on every single item
and every single version of the app." Continues .claude/plans/responsive-full-audit-2026-11-21.md
(stalled after its plan; ResponsiveAuditTests.cs now lists every route plus a guard test).
- [ ] Run ResponsiveAuditTests against the local app on the DB copy (:5299, user localdiag):
      Chromium, all 12 devices; WebKit (GWS_PLAYWRIGHT_BROWSER=webkit = Safari / iPhone PWA / Mac
      app WebView), all 12 devices. Also GWS_AUDIT_TEXT_SCALE=2 on phones.
- [ ] Triage audit.md + look at the screenshots per page (cramped, overlapping, cut off).
- [ ] Mac app: native chrome (toolbar, sidebar) at narrow/wide window sizes via screencapture.
- [ ] Fix at the root (shared components first), re-run until clean, release gate, report.

## Findings from the tours (27-11-26) and what was done
- Real Mac captures work (screencapture -l from scratchpad/wid/watch.sh; app writes shot-request.txt).
- Mac look in orbit = Apple's real-imagery globe (atmosphere, stars, green coasts/borders, white names,
  real day/night). Web Standard/Night Ops now: Esri imagery at every height (fades out as the green
  street map fades in), no land fill/grid, atmosphere + stars, place names and faint roads over imagery.
  Not matched: the Mac's night side with city lights (needs Cesium lighting; banned for WebKit) - ask Grant.
- Mac fly-to was 4x too high and 180 deg off at world scale (span = height/55,000 via SetRegion): now
  MKMapCamera at the requested height. Explains Mac aircraft "none" (out of the 5x5 deg window).
- Mac crash (SIGSEGV in MKQuadTrie insert from addAnnotations) after turning 3D on mid-tour: pin updates
  now wait 2 s after a map configuration change; RegroupIfZoomed skips meanwhile. Not reproducible on
  demand (3D-only run didn't crash), so this is hardening of the known MapKit fragility.
- Hazards: GDACS hanging to the 25 s timeout and failures not remembered -> every refresh waited 25 s and
  drew nothing. Failed sources now cached empty for 5 min (HazardSourceFailureTests).
- Late answers drew switched-off layers (quake pins after Hazards off): all 16 layer pushes re-check their
  switch after the await (OverwatchLateLayerAnswerTests).
- Web groups conflict reports like the Mac; a group stacked on one spot opens its first item.
- Tour fixes: tabs (coverage/route/favorites/areas are inspector tabs), nested switches; visiblePins
  uses drill pick (aircraft under thermal glow) and a source filter.
- Not clickable by design on both: power outages, air quality, ships. Live flow needs TomTom (prod has it).
- Mac crash, deeper (27-11-26): both full tours crashed with EXC_BAD_ACCESS at 0x20 in MKQuadTrie insert
  (same as 19-11-26). Reproduced with the tour's 'stress' step (GWS_OVERWATCH_SELFTEST_ONLY=stress:
  24 long flies while switching incidents/conflict/news/hazards/fires/aircraft off and on): 1 crash in 2
  runs before the fix. Theory: many pins on one exact spot (news at country centres, conflict at one
  town) break MapKit's quad trie. Fix: the bridge spreads exact duplicates on a ~11 m x sqrt(n)
  spiral (NativeShapes.Spread, NativeShapesSpreadTests). 3D now aims at the fly-to target mid-flight.
- Crash outcome (27-11-26): NOT fixed. Tried and REMOVED (didn't stop it, added delays): spreading
  stacked pins, holding pin changes while the map moves, taking pins off across 3D switches. 4 of 4 full
  tours crashed (EXC_BAD_ACCESS 0x20, MKQuadTrie insert from addAnnotations), 3 at the same point: the
  steps after 3D/contours/slope toggled the 3D configuration 3x (coverage/healthy). Callers: WebKit
  script message (layer update) and once MapKit's region callback. Next idea: never switch
  PreferredConfiguration (keep MapType HybridFlyover and only change camera pitch for 3D), then rerun
  the full tour; or rebuild MKMapView after a configuration change.
- Kept on the Mac: fly-to at the requested height/centre, 3D aims at the flight's destination, 3D hides
  Apple POIs. Web: tablet toolbar title hidden <=900 (no overlap); phone sheets --ow-glass-sheet.
- Progress: units 1-4 done; 5 (tests/gate/guide) in progress; 6 done (Chromium 1440, WebKit 1440/820/390);
  7 done with the crash open; 8: report + commit message next, then Phase 2 (responsive).
