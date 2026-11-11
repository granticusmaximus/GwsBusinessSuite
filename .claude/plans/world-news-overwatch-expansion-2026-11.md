# World News & Overwatch Enhancement Plan

## Granular Milestones

- [ ] Unit 1: Outlet Ingestion & Regional Media Expansion (`OutletFeeds.cs`, `GoogleNewsFeed.cs`, `WorldNewsMapService.cs`)
  - [ ] 1.1 Expand `OutletFeeds.Outlets` with key regional, Middle Eastern, Asian, European, and Latin American publishers
  - [ ] 1.2 Implement adaptive outlet threshold for low-density/conflict zones (allow 1-2 outlets if flagged with local wire/conflict)
  - [ ] 1.3 Add unit tests in `tests/GwsBusinessSuite.Tests/` to verify feed parsing and adaptive thresholds
  - [ ] 1.4 Run tests to verify Unit 1 passes

- [x] Unit 2: Viewport-Adaptive Geo-Querying (`WorldNewsMapService.cs`, `OverwatchGrid.razor`, `tactical-globe.js`)
  - [x] 2.1 Existing `WorldNewsMapService.SignalsAt` filters mapped situations to the requested bounds.
  - [x] 2.2 Existing Cesium `camera.moveEnd` handler debounces settled moves and calls `OnGlobeViewChanged`; the component queries World News by those bounds.
  - [x] 2.3 Added an explicit viewport-only regression test to `WorldNewsMapServiceTests`.
  - [x] 2.4 Focused World News map tests pass (17 tests).

- [x] Unit 3: "Brief This View" AI Tactical Intelligence Action (`OverwatchBriefingService.cs`, `OverwatchGrid.razor`)
  - [x] 3.1 Existing `GetViewFactsAsync` gathers bounding-box incidents, weather, hazards, cameras, World News and convergence; it now also adds bounded aircraft/AIS facts.
  - [x] 3.2 Existing HUD Brief View action now writes with configured server Ollama when `ServerAi` is `Light`/`Full`; `None` retains the plain facts fallback.
  - [x] 3.3 Added tests for viewport sensor facts and server-mode/model selection.
  - [x] 3.4 Focused briefing tests pass.

- [ ] Unit 4: Reporting Blackout & Silence Detection (`SignalConvergenceService.cs`, `tactical-globe.js`)
  - [ ] 4.1 Implement silence detection logic (IODA internet outages + NASA fire/conflict signals without news coverage)
  - [ ] 4.2 Add visual dashed perimeter representation for reporting blackouts in Cesium
  - [ ] 4.3 Add unit tests verifying reporting blackout criteria
  - [ ] 4.4 Run tests to verify Unit 4 passes

- [x] Unit 5: Multi-Sensor Correlation Dossier in Story Panel (`tactical-globe.js`, `OverwatchGrid.razor`)
  - [x] 5.1 `WorldNewsContextService` queries the existing bounded OpenSky and AIS services for city-level stories.
  - [x] 5.2 The story panel labels aircraft/vessels by source and states nearby sensors do not confirm the reported event.
  - [x] 5.3 Added service tests for 25-mile filtering and result caps, plus browser-contract assertions.
  - [x] 5.4 Focused World News context tests pass; browser test source compiles but needs Playwright Chromium to execute.

- [ ] Unit 6: Full Verification & Release Readiness
  - [ ] 6.1 Run full solution test suite
  - [ ] 6.2 Confirm all gates pass cleanly

## 10-11-26 implementation note

- The viewport feature was already implemented before this work; only its plan status and explicit
  bounding-box regression coverage were missing.
- Brief View prose now uses `IOllamaService` and `ContentStudio:Model` only when server AI mode is
  `Light` or `Full`. Compose retains `None` by default; `SERVER_AI_MODE=Light` and
  `ContentStudio__BaseUrl` must be set in the private `.env` to use the separate inference host.
- The full suite/browser test and release gate remain unverified here. The docs directory is
  absent from this checkout.
