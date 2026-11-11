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
- [ ] Add the trusted native-map bridge and lifecycle management.
- [ ] Add the JavaScript native adapter with complete command forwarding and return semantics.
- [ ] Implement native annotations, clustering, overlays, routes, camera controls, picking, and
  selection callbacks.
- [ ] Map every existing Overwatch operation and prove contract/adapter parity with tests.
- [ ] Remove the obsolete native shader interception and GPU-unavailable fallback from the Mac path.
- [ ] Build and run authenticated native acceptance on this Mac.
- [ ] Run focused browser/PWA regression tests.
- [ ] Run `./scripts/verify-release.sh`.

## Acceptance

- Overwatch never leaves the Mac app.
- The Mac path never initializes Cesium or displays the GPU/browser-unavailable message.
- Every operation in `NativeOverwatchOperations.All` has an explicit native rendering or native
  interaction implementation; unsupported silent no-ops are not acceptable.
- Pins do not bleed through the back of the map, clustered counts are visible, and selecting an
  actionable pin opens the same detail workflow as the web renderer.
- Browser and PWA behavior remains unchanged.
