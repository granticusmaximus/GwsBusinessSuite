# Overwatch coverage (October batch item 9) - started 2026-10-04

Grant: "Start work on ... the deferred Overwatch coverage" (2026-10-04). Same standard as the master
plan's Workstream A (humming-mapping-hartmanis.md A.1-A.3): official, free, no-key feeds only,
every endpoint verified live before building, [JsonPropertyName] on every field, one HttpClient
per provider type.

## Done 2026-10-04 - WZDx incidents batch 2 (verified, verify-release PASS)

- 14 new key-less WZDx work-zone feeds from USDOT's registry (datahub.transportation.gov
  69qe-yiui): FL, MD, WI, WA, KY, NJ, KS, IN, NC, MS, DE, LA, MO work zones, Austin TX. Each fetched
  live through the real provider. Counts of current work on 2026-10-04: FL 1629, Austin 1838, IN 948,
  WI 725, MO 533, NJ 434, NC 378, WA 368, KS 337, KY 123, MS 119, MD 83, DE 13, LA 0.
- WzdxIncidentProvider now keeps only current work (start/end dates, "completed" status) - FL had
  26,109 entries, mostly planned/finished.
- Incident pins are clustered on the globe (amber), like cameras.
- "wzdx" HttpClient: automatic decompression (FL always gzips) + User-Agent (WI 403s without).
- Excluded: NY (registry URL serves an SPA), NM (blyncsy 503 - retry), MN (CARS511 events already
  include roadwork), county feeds inside covered states, all key-required feeds.

## Next: cameras - leads verified live 2026-10-04 (not built yet)

| Lead | Endpoint | Notes |
| --- | --- | --- |
| Singapore | https://api.data.gov.sg/v1/transport/traffic-images | 200, no key (guide lists SG as key-gated - that's stale). camera_id, location.latitude/longitude, image URL that rotates every ~1 min - re-fetch list per refresh, cache ~1 min |
| Hong Kong | https://static.data.gov.hk/td/traffic-snapshot-images/code/Traffic_Camera_Locations_En.csv | 200, 377KB CSV (non-UTF8 bytes - check encoding). Images at tdcctv.data.one.gov.hk/{key}.JPG (verify) |
| Iceland | https://gagnaveita.vegagerdin.is/api/vefmyndavelar2014_1 | 200 JSON: Myndavel (name), Skyring (desc), Slod (direct jpg), Breidd (lat), Lengd (lon). Very clean |
| MN/NE plow cams | services.arcgis.com/8lRhdTsQyJpO52F1 .../AVL_Plow_Cam_Images_{Minnesota,Nebraska}_View/FeatureServer/0 | snowplow camera stills (moving, seasonal); PHOTO_LATITUDE/LONGITUDE fields; check image URL field + freshness |
| Tennessee | spatial.tdot.tn.gov/arcgis/rest/services (folder "Smartway") | check for a camera layer |
| Colorado | api-511x-co.carsprogram.org | successor to the retired cotrip endpoint; camera API undocumented |

Never researched yet: AK, NM, SD, WV cameras; MA re-check; Taiwan, Norway, Denmark, Estonia,
Switzerland; Canada NS/NB/MB/SK/YT/NL/PE; Australia WA/SA/TAS/NT.
- Resolved: the 688s suite run on 2026-10-04 was machine load; it ran in 124s on 2026-10-05 with no code change.
