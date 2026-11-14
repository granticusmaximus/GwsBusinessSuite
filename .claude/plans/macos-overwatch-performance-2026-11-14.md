# macOS Overwatch native performance (14-11-26)

Grant asked to implement the full native-performance plan after the MapKit icon pass.

## Acceptance targets

- Stable objects update in place; moving aircraft, ships and satellites are not removed/re-added.
- Superseded native layer revisions are coalesced before MapKit work.
- Small pans inside prefetched coverage do not refetch every enabled layer.
- Moving layers send deltas rather than full static metadata and geometry.
- Payload parsing/diff/geometry preparation stays off the main thread; only MapKit mutation is main-thread.
- Radar/satellite parent tiles and generated child tiles are cached and in-flight requests coalesced.
- The authenticated native viewport endpoint uses the existing governed command contract.
- Dense layers use cached lightweight symbol views where that preserves clustering and hit targets.
- Layer status UI rerenders are batched; background/hidden Overwatch pauses high-frequency work.
- Performance counters cover payload bytes, prepare/apply time, object churn, coalescing and tile cache.

## Work order

1. Stable IDs, in-place annotation updates and O(1) native-handle resolution.
2. Revision coalescing and off-main preparation.
3. Viewport hysteresis, expanded query coverage and cancellation.
4. Generic point-layer delta commands; position-only moving-layer refreshes.
5. Overlay/geometry revisioning and zoom-aware simplification.
6. Tile caching, request coalescing and cancellation.
7. Authenticated native viewport endpoint and direct Mac client path.
8. Dense-layer lightweight annotation views, render batching and lifecycle throttling.
9. Instrumentation, focused benchmarks, user guide, native build and release gate.

## Progress

- 14-11-26: checkout clean/current at `39e3bd5`; source audit complete. The highest-cost confirmed
  path is full JSON payload identity in `MacNativeOverwatchBridge.Render`: any changing field makes
  a moving object a remove/add. Implementation started with item 1.
