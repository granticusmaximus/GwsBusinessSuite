# macOS Overwatch zoom stability

## Evidence

- The native map log ends after rapid zoom and a 1,500-camera layer application.
- There is no GWS crash report or managed/native exception for that event.
- Superseded viewport camera-provider calls continued running until their source deadlines.
- Two local Ollama models were simultaneously resident at about 5.8 GB and 4.8 GB.

## Progress

- [x] Cancel superseded camera-provider work when a newer viewport arrives.
- [x] Bound native MapKit camera annotations at 750; retain the full in-view count.
- [x] Reduce Mac-host Ollama residency from 30 minutes to 5 minutes.
- [x] Add cancellation and model-residency regression tests.
- [x] Run focused tests, explicit Mac Catalyst build, and the release gate (`PARTIAL` only because deployed targets were not supplied).

## Validation boundary

Local tests and builds do not prove the original physical zoom gesture. Final acceptance requires
repeating rapid zoom in the installed Mac app while dense camera coverage is visible.
