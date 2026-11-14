# macOS MapKit native icons (14-11-26)

Grant asked for native Overwatch MapKit pins to show an icon matching the object (camera, plane,
ship, hazard subtype, and so on) instead of letters or a generic bullet.

## Plan

1. Add a platform-neutral, payload-aware marker-style resolver beside `NativeShapes`.
2. Render its SF Symbol and semantic colour in `MacNativeOverwatchBridge`, with safe reuse and fallback.
3. Cover operations and important subtypes with focused tests.
4. Update the Mac user guide and run `./scripts/verify-release.sh`.

## Progress

- 14-11-26: investigation complete. The payload already carries the required operation/subtype data;
  SF Symbols cover the inventory except for a literal satellite silhouette, where the native antenna
  symbol is the clearest system fallback. Implementation started.
- 14-11-26: implementation complete. `NativeShapes.Marker` maps layer and subtype to an SF Symbol,
  semantic colour and prominence. MapKit uses glyph images with a safe system fallback, preserves
  numeric clusters, and resets reused views. Focused tests pass (24); the Mac Catalyst Debug build
  passes with zero warnings. User guide updated.
- 14-11-26: final focused native contract set passes (33 tests). `./scripts/verify-release.sh`
  completed PARTIAL: lint, restore, vulnerability audit, Release build and all 4,390 automated tests
  passed; deployed/responsive targets were not supplied. The gate did not repeat its Mac build, so
  the separate successful Mac Catalyst Debug build above is the native compile evidence. Live native
  visual acceptance remains for the installed updated Mac app.
