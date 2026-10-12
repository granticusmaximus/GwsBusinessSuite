# Overwatch standout features (approved 2026-10-11)

Grant asked how to make Overwatch stand out, then: "Proceed with all". Builds on the 2026-10
redesign (overwatch-redesign-2026-10.md). This file is the tracker; every item is verified with
`./scripts/verify-release.sh` and OVERWATCH_USER_GUIDE.md is updated in the same change.

Rules: local AI only (the browser machine's/Mac's Ollama, as Ask the Cameras does today); free
public data sources only, or ones Grant supplies a free key for; dates dd-MM-yy and 24-hour times;
no inline scripts (CSP); AdminOnly like the rest of Overwatch.

## Items (in build order)

| # | Feature | Approach | Status |
| --- | --- | --- | --- |
| 1 | When should I leave? | For the planned route, try departures every 30 min over the next 12 h; at each sample point's arrival time read the NWS hourly forecast (rain/snow/storm chance, temperature for ice), sun glare/darkness (DrivingLightAnalyzer) and incidents/closures active then; score and chart; recommend the best time with reasons | done 2026-10-11: DeparturePlanner + DeparturePlannerService (NWS hourly per ~25-mile sample point, glare/dark via DrivingLightAnalyzer, closures by EndsAt, crashes only within 90 min); DIRECTIONS tab chart (25 bars), best time, USE THIS TIME sets LEAVE AT; 5 tests |
| 2 | Camera-first incident detection | Background watcher runs the local vision model over favorites + cameras in watched areas every N minutes (on the Mac/browser machine's Ollama via the existing camera-conditions path, or skipped when none is reachable); a change to bad (stopped traffic, snow, ice, flooding, smoke) raises one bell alert with before/after frames | not started |
| 3 | Time machine | One scrubber over the past 24 h: radar (past frames), incidents (history table), hazards, and saved camera frames shown together at the chosen time | not started |
| 4 | AI briefings | "Brief me" for the current view/area/trip: local model writes a short briefing citing the cameras, incidents, alerts it used; daily morning briefing for saved trips + watched areas to the bell (and email if SMTP is set) | not started |
| 5 | Drive mode | Follow a planned route live with the device's GPS: next maneuver, cameras ahead auto-open, spoken warnings (browser speech), reroute suggestion when a closure appears ahead | not started |
| 6 | 3D cities + smooth street view | Fly-down transition from globe into street view; 3D buildings/terrain need a free Cesium ion token (ask Grant when reached) | not started |
| 7 | Map notes | Pins, notes and drawn areas on the globe, saved per user, shareable by link | not started |
| 8 | Incident report export | One click: PDF timeline of an incident - camera frames, incident details, weather, map snapshot | not started |
| 9 | More live layers | Aircraft (OpenSky, keyless), air quality (Open-Meteo, keyless); ships (aisstream.io, free key - ask) and power outages (no free nationwide source - report back) | not started |
| 10 | Coverage-gap helper | When a view has no cameras/incidents: say so and point to the nearest covered area with a GO button | not started |
| 11 | Performance | Measure load with many layers on; fix the slowest paths (request batching, caching, entity limits) | not started |

## Decisions needed from Grant (asked when the item is reached)

- Item 6: Grant created a Cesium ion account (2026-10-11); give him token steps when item 6 starts.
- Item 9: create a free aisstream.io key for ships?
