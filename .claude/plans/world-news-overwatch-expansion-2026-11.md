# World News & Overwatch Enhancement Plan

## Granular Milestones

- [ ] Unit 1: Outlet Ingestion & Regional Media Expansion (`OutletFeeds.cs`, `GoogleNewsFeed.cs`, `WorldNewsMapService.cs`)
  - [ ] 1.1 Expand `OutletFeeds.Outlets` with key regional, Middle Eastern, Asian, European, and Latin American publishers
  - [ ] 1.2 Implement adaptive outlet threshold for low-density/conflict zones (allow 1-2 outlets if flagged with local wire/conflict)
  - [ ] 1.3 Add unit tests in `tests/GwsBusinessSuite.Tests/` to verify feed parsing and adaptive thresholds
  - [ ] 1.4 Run tests to verify Unit 1 passes

- [ ] Unit 2: Viewport-Adaptive Geo-Querying (`WorldNewsMapService.cs`, `OverwatchGrid.razor`, `tactical-globe.js`)
  - [ ] 2.1 Add viewport-based bounding-box news querying endpoint / service method in `WorldNewsMapService`
  - [ ] 2.2 Wire viewport settle event in `tactical-globe.js` to request location-scoped stories when zoomed in (<2000 km)
  - [ ] 2.3 Add unit tests for bounding box query logic and distance filtering
  - [ ] 2.4 Run tests to verify Unit 2 passes

- [ ] Unit 3: "Brief This View" AI Tactical Intelligence Action (`OverwatchBriefingService.cs`, `OverwatchGrid.razor`)
  - [ ] 3.1 Implement `BriefViewportAsync(BoundingBox bounds)` in `OverwatchBriefingService` aggregating news, hazards, flights, and weather in view
  - [ ] 3.2 Wire `BRIEF VIEW` button in Overwatch HUD to trigger viewport briefing modal
  - [ ] 3.3 Add unit tests in `tests/GwsBusinessSuite.Tests/` for viewport briefing aggregation
  - [ ] 3.4 Run tests to verify Unit 3 passes

- [ ] Unit 4: Reporting Blackout & Silence Detection (`SignalConvergenceService.cs`, `tactical-globe.js`)
  - [ ] 4.1 Implement silence detection logic (IODA internet outages + NASA fire/conflict signals without news coverage)
  - [ ] 4.2 Add visual dashed perimeter representation for reporting blackouts in Cesium
  - [ ] 4.3 Add unit tests verifying reporting blackout criteria
  - [ ] 4.4 Run tests to verify Unit 4 passes

- [ ] Unit 5: Multi-Sensor Correlation Dossier in Story Panel (`tactical-globe.js`, `OverwatchGrid.razor`)
  - [ ] 5.1 Expand `WorldNewsContextAsync` to query nearby AIS ships and ADS-B aircraft
  - [ ] 5.2 Display nearby flights and vessels in the Overwatch story detail panel
  - [ ] 5.3 Add unit tests for expanded story context
  - [ ] 5.4 Run tests to verify Unit 5 passes

- [ ] Unit 6: Full Verification & Release Readiness
  - [ ] 6.1 Run full solution test suite
  - [ ] 6.2 Confirm all gates pass cleanly
