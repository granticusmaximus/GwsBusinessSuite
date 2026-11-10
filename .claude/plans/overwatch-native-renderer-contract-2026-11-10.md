# Overwatch native renderer contract

## Goal

Provide a versioned, authenticated native-renderer data boundary without changing the existing
Cesium/web route. The Mac renderer can consume the same payload command names and controls that
`OverwatchGrid.razor` sends to `tactical-globe.js`.

## Progress

- [x] Inventory the existing JS interop operations and viewport controls.
- [x] Add the versioned shared contract and authenticated API surface.
- [x] Add contract coverage tests and update the Overwatch user guide.
- [ ] Run release verification.

## Notes

This is deliberately additive: `/admin/osint`, Cesium, and the Mac WebView navigation remain
unchanged. The native renderer is not routed or enabled by this work.
