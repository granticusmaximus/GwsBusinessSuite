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

## Acceptance

- Overwatch never leaves the Mac app.
- The Mac path never initializes Cesium or displays the GPU/browser-unavailable message.
- Every operation in `NativeOverwatchOperations.All` has an explicit native rendering or native
  interaction implementation; unsupported silent no-ops are not acceptable.
- Pins do not bleed through the back of the map, clustered counts are visible, and selecting an
  actionable pin opens the same detail workflow as the web renderer.
- Browser and PWA behavior remains unchanged.
