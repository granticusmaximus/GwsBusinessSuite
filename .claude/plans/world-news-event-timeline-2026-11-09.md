# World News event timeline and Overwatch replay

## Goal

Extend the existing World News situation/event model into a user-controlled event timeline that
can filter the Overwatch World News layer by a selected point in the recent reporting window.
Reuse `WorldNewsStore`, `WorldNewsMapService`, and the existing Overwatch time-window selector;
do not create a second news store or timeline model.

## Scope

1. Audit the existing event, situation, map signal, and Overwatch interop contracts.
2. Add a bounded replay point to the existing World News map query so a selected time shows only
   signals known by that time.
3. Add an accessible Overwatch timeline control and show its selected point in the World News
   panel.
4. Preserve live behavior when the replay is at "Now"; layers that have no historical data remain
   explicitly live-only.
5. Add focused store/map and component-contract tests, update the World News user guide, and run
   the full release gate.

## Progress

- [x] Confirmed the checkout is current with `origin/main`.
- [x] Reviewed repository, Claude, and Codex guidance plus relevant Overwatch/World News handoffs.
- [x] Trace the existing Overwatch time-window and JavaScript interop contracts.
- [x] Implement the map-query replay boundary and focused regression tests.
- [x] Implement the Overwatch timeline control and its browser contract.
- [x] Update user documentation.
- [ ] Run `./scripts/verify-release.sh` and record the result.

