# Overwatch globe pin integrity

## Goal

Keep markers on the visible hemisphere, make clustered counts legible, and make overlapping
actionable pins reliably open their panel. Preserve the unfinished native-renderer contract as an
additive foundation; do not route users to its placeholder host.

## Progress

- [x] Audit Copilot's native-renderer commits and identify the actual completion boundary.
- [x] Reproduce the pin mechanisms in the real Cesium browser harness.
- [ ] Restore globe depth testing for camera, incident, and World News clusters.
- [ ] Ensure cluster count labels are not covered by Cesium's default point primitive.
- [ ] Prefer actionable objects when several primitives overlap at the clicked pixel.
- [ ] Add focused regression coverage and update the Overwatch user guide.
- [ ] Run the focused suite and `./scripts/verify-release.sh`.

## Native renderer handoff

Copilot added contract discovery, a contract client, a lossless payload adapter, and an unconnected
UIKit placeholder. It did not add an authenticated viewport payload endpoint, native map drawing,
or route wiring. `GWS_NATIVE_OVERWATCH_RENDERER` therefore remains intentionally inactive until a
real renderer reaches feature parity; enabling the placeholder would regress the working product.
