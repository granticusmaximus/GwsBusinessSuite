# World News viewport, briefing, and sensor correlation (10-11-26)

## Audit

- Viewport-adaptive World News querying is already implemented: Cesium debounces settled camera
  moves, `OnGlobeViewChanged` passes the bounding box, and `WorldNewsMapService.SignalsAt` filters
  placed stories to it. The older expansion plan's Unit 2 checkbox is stale.
- Brief View already gathers viewport incidents, alerts, hazards, cameras, World News, and signal
  convergence. Its prose generation still probes `BrowserLocalOllamaService`, contrary to the
  separate inference-host architecture.
- The World News story dossier already reports official backing, cameras, alerts, hazards, and
  outages. It does not yet include nearby OpenSky aircraft or AIS vessels.
- Checked-in Compose currently hard-codes `ServerAi__Mode: None`; that selects the Mac relay. Keep
  `None` as the safe default, but allow an explicit mode override and use the configured
  `ContentStudio:BaseUrl` for server inference in `Light` mode.

## Progress

- [x] Add an explicit viewport-only regression test and reconcile the stale Unit 2 status.
- [x] Route Brief View prose through `IOllamaService` and configured `ContentStudio:Model`, only
  when server inference is enabled; retain a plain-facts fallback when it is disabled.
- [x] Add bounded nearby aircraft/AIS context to city-level story dossiers and viewport brief
  facts. Keep sensor reports contextual, not evidence that a story occurred.
- [x] Add focused service tests and browser/source-contract coverage.
- [x] Focused service tests pass; the browser test source compiles but cannot execute here because
  Chromium is absent and Playwright's browser download failed certificate validation.
- [ ] Run the full test suite and `./scripts/verify-release.sh` on a supported environment.
- [ ] Update user documentation if the ignored `docs/` guide is available.

## Runtime configuration

The app must be configured with `ServerAi:Mode=Light` and `ContentStudio:BaseUrl` pointing to the
separate inference host to use it. No endpoint or credentials are hard-coded here; `None` remains
the Compose default until an operator opts in through the local `.env`.

## Verification

- World News map/context, viewport sensor facts, and server-mode Brief View focused tests passed
  (24 total across the focused runs); Web, Infrastructure, and test projects compile.
- `verify-release.sh --server-only --skip-tests` passed restore, dependency audit, and Release
  build; Docker Compose and responsive lint could not run because Docker and Python are unavailable.
- The docs directory is absent from this checkout; no endpoint or credentials were added to tracked files.
- The final default `verify-release.sh` run again failed restore/build on missing MAUI workloads;
  its full test host remained idle beyond five minutes and was stopped. No Docker or browser was
  installed.
- After the final code changes, `verify-release.sh --server-only --skip-tests` passed restore,
  vulnerability audit, and Release build; Docker Compose and Python lint remain unavailable.