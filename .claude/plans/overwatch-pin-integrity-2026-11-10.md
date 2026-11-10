# Overwatch globe pin integrity

## Goal

Keep markers on the visible hemisphere, make clustered counts legible, and make overlapping
actionable pins reliably open their panel. Preserve the unfinished native-renderer contract as an
additive foundation; do not route users to its placeholder host.

## Progress

- [x] Audit Copilot's native-renderer commits and identify the actual completion boundary.
- [x] Reproduce the pin mechanisms in the real Cesium browser harness.
- [x] Restore globe depth testing for camera, incident, and World News cluster billboards and labels.
- [x] Hide Cesium's default cluster point primitive so count labels remain visible.
- [x] Prefer actionable objects when several primitives overlap at the clicked pixel.
- [x] Add focused source-contract coverage and a real-browser overlap regression.
- [ ] Execute the browser overlap regression; local Playwright Chromium is unavailable and its download failed certificate validation.
- [ ] Update the Overwatch user guide; the ignored `docs/` directory is absent from this checkout.
- [x] Run focused source-contract tests (2 passed).
- [ ] Execute the real-browser overlap test; local Chromium is missing and Playwright's download failed certificate validation.
- [ ] Complete the full release gate; see `sync-and-overwatch-closure-2026-11-10.md` for the environment blockers.

The focused source-contract tests passed on 10-11-26. Native Mac acceptance remains a separate
device-dependent check; see `sync-and-overwatch-closure-2026-11-10.md`.

## Native renderer handoff

Copilot added contract discovery, a contract client, a lossless payload adapter, and an unconnected
UIKit placeholder. It did not add an authenticated viewport payload endpoint, native map drawing,
or route wiring. `GWS_NATIVE_OVERWATCH_RENDERER` therefore remains intentionally inactive until a
real renderer reaches feature parity; enabling the placeholder would regress the working product.
