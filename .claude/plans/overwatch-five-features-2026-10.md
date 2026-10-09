# Overwatch: five new features (requested 2026-10-09)

Grant picked all five proposed features ("work on the other features I had you research"), after
the radar timeline (done 2026-10-09: past hour + HRRR future radar + NWS hourly at the scrubbed time).
Build order and status - each verified with `./scripts/verify-release.sh` before it's marked done.

| # | Feature | Status |
| --- | --- | --- |
| 1 | Sun glare + darkness forecast for trips (sun position vs. road heading at each leg's ETA; no data source) | built 2026-10-09 (DrivingLightAnalyzer; LEAVE AT on the route panel; amber/violet stretches on the globe) |
| 2 | Live highway message signs (Iteris `icons.dms.geojson`; MT verified 76 signs, SD 403) | built 2026-10-09: SIGNS toggle + route list; Caltrans CWWP2 cms (407 lit) + 511MT (76) |
| 3 | Incident auto-capture: new incident in a watch area -> nearest cameras' frames saved before/after | not started |
| 4 | Road-surface / ice risk from RWIS surface readings (Iteris `icons.rwis.geojson` surface[]) | not started |
| 5 | Ask the cameras along a route: frames -> local vision model on the Mac (browser relay) -> condition strip | not started |
